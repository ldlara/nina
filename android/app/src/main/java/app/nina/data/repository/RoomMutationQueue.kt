package app.nina.data.repository

import androidx.room.withTransaction
import app.nina.data.local.MutationDao
import app.nina.data.local.NinaDatabase
import app.nina.data.local.TrackingDao
import app.nina.domain.model.MutationOp
import app.nina.domain.tracking.MutationQueue
import app.nina.domain.tracking.QueuedMutation
import app.nina.domain.tracking.SyncState
import kotlinx.coroutines.flow.Flow
import java.time.Instant

/** Fila de mutações sobre Room. Cada transição é transacional junto com o estado do registro correspondente. */
class RoomMutationQueue(
    private val database: NinaDatabase,
    private val mutations: MutationDao,
    private val events: TrackingDao,
) : MutationQueue {

    override fun observePendingCount(): Flow<Int> = mutations.observePendingCount()
    override fun observeRejectedCount(): Flow<Int> = mutations.observeRejectedCount()

    override suspend fun nextBatch(now: Instant, limit: Int): List<QueuedMutation> {
        // Um lote por vez: enquanto algo está em voo, nada novo sai (ordem e ausência de envio duplicado).
        if (mutations.inFlight().isNotEmpty()) return emptyList()
        val nowMs = now.toEpochMilli()
        val batch = ArrayList<QueuedMutation>()
        for (m in mutations.sendable(limit.coerceIn(1, MutationQueue.MAX_BATCH))) {
            // FIFO estrito: uma mutação em backoff segura as mais novas (não passam à frente dela).
            if (m.nextAttemptAt > nowMs) break
            batch += m.toDomain()
        }
        return batch
    }

    override suspend fun markInFlight(mutationIds: List<String>) {
        if (mutationIds.isNotEmpty()) mutations.markInFlight(mutationIds)
    }

    override suspend fun markApplied(mutationId: String, serverVersion: Int?) {
        database.withTransaction {
            val m = mutations.get(mutationId) ?: return@withTransaction // já aplicada (DUPLICATE/reenvio): idempotente
            mutations.delete(mutationId)
            val event = events.get(m.entityId)
            if (m.op == MutationOp.DELETE.name) {
                if (mutations.countActiveForEntity(m.entityId) == 0) events.hardDelete(m.entityId)
            } else if (event != null) {
                val version = serverVersion ?: event.version
                // Mutações seguintes do mesmo registro passam a partir da versão que o servidor acabou de atribuir.
                if (serverVersion != null) mutations.rebase(m.entityId, serverVersion)
                val state = if (mutations.countActiveForEntity(m.entityId) > 0) SyncState.PENDING else SyncState.SYNCED
                events.setVersionAndState(m.entityId, version, state.name)
            }
        }
    }

    override suspend fun markRetry(mutationId: String, errorCode: String, nextAttemptAt: Instant) {
        mutations.markRetry(mutationId, errorCode, nextAttemptAt.toEpochMilli())
    }

    override suspend fun markRejected(mutationId: String, errorCode: String) {
        database.withTransaction {
            val m = mutations.get(mutationId) ?: return@withTransaction
            mutations.markRejected(mutationId, errorCode)
            if (events.get(m.entityId) != null) events.setSyncState(m.entityId, SyncState.FAILED.name)
        }
    }

    override suspend fun releaseInFlight() = mutations.releaseInFlight()
    override suspend fun retryNow() = mutations.retryNow()
    override suspend fun snapshot(): List<QueuedMutation> = mutations.all().map { it.toDomain() }
}
