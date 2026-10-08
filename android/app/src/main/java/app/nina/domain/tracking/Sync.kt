package app.nina.domain.tracking

import app.nina.domain.model.MutationOp
import app.nina.domain.model.SyncEntityType
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.JsonObject
import java.time.Instant

enum class MutationStatus {
    /** Aguardando envio. */
    PENDING,

    /** Entregue ao motor de sync, sem resposta ainda. Volta a PENDING se o app morrer (ver [MutationQueue.releaseInFlight]). */
    IN_FLIGHT,

    /** Falha transitória; será reenviada a partir de [QueuedMutation.nextAttemptAt]. */
    RETRY,

    /** Recusada sem possibilidade de reenvio (`REJECTED`, `retryable=false`). Fica para diagnóstico/descarte pelo usuário. */
    REJECTED,
}

/**
 * Mutação da fila (RF-046-A3, ADR-0003). [mutationId] é a chave de idempotência; [clientCreatedAt] é só informativo
 * (SR-014: a ordem de resolução é do servidor); [baseVersion] é `0` na criação.
 */
data class QueuedMutation(
    /** Ordem de inserção (estritamente crescente); define a ordem de envio. */
    val seq: Long,
    val mutationId: String,
    val babyId: String,
    val entityType: SyncEntityType,
    val entityId: String,
    val op: MutationOp,
    val baseVersion: Int,
    val clientCreatedAt: Instant,
    val deviceId: String,
    /** `data` do contrato (`SleepData`, `EventPatch`...); `null` em `DELETE`. */
    val data: JsonObject?,
    val status: MutationStatus,
    val attempts: Int,
    val nextAttemptAt: Instant,
    val lastErrorCode: String?,
)

/**
 * Fila de mutações offline. É a superfície que o motor de sync de rede (Onda 5) consome:
 * `nextBatch` -> `markInFlight` -> `markApplied` / `markRetry` / `markRejected`.
 */
interface MutationQueue {
    /** Mutações que ainda precisam sair (PENDING, IN_FLIGHT, RETRY). */
    fun observePendingCount(): Flow<Int>

    /** Mutações recusadas em definitivo, aguardando decisão. */
    fun observeRejectedCount(): Flow<Int>

    /**
     * Próximo lote em ordem de inserção. Para no primeiro item ainda não elegível (retry com backoff), preservando a
     * ordem: nada passa na frente de uma mutação mais antiga do mesmo dispositivo. Máximo 100 (`sync_max_mutations_per_push`).
     */
    suspend fun nextBatch(now: Instant, limit: Int = MAX_BATCH): List<QueuedMutation>

    suspend fun markInFlight(mutationIds: List<String>)

    /** `APPLIED` ou `DUPLICATE`: remove a mutação e atualiza a versão/estado do registro. Idempotente. */
    suspend fun markApplied(mutationId: String, serverVersion: Int?)

    /** Falha transitória (`TRANSIENT`, rede, 5xx, 429): mantém na fila, conta a tentativa e agenda [nextAttemptAt]. */
    suspend fun markRetry(mutationId: String, errorCode: String, nextAttemptAt: Instant)

    /** `REJECTED` com `retryable=false`: sai do fluxo de envio e marca o registro como `FAILED`. */
    suspend fun markRejected(mutationId: String, errorCode: String)

    /** Após reinício do app/processo: o que estava em voo volta a PENDING (RF-046-A4/A5). */
    suspend fun releaseInFlight()

    /** O usuário pede para tentar de novo agora: zera o backoff e devolve RETRY/REJECTED a PENDING. */
    suspend fun retryNow()

    suspend fun snapshot(): List<QueuedMutation>

    companion object {
        const val MAX_BATCH = 100
    }
}

sealed interface SyncStatus {
    data object Idle : SyncStatus
    data object Syncing : SyncStatus

    /** Falha persistente desde [since] (ux-spec 4.13). */
    data class Error(val since: Instant, val code: String?) : SyncStatus
}

sealed interface SyncRunResult {
    data object Done : SyncRunResult
    data object Offline : SyncRunResult

    /** O motor real ainda não existe (Onda 5). A UI informa que os dados estão salvos neste aparelho. */
    data object NotAvailable : SyncRunResult
    data class Failed(val code: String?) : SyncRunResult
}

/** Contrato do motor de sync de rede. A implementação real é da Onda 5; aqui há [StubSyncEngine]. */
interface SyncEngine {
    val status: StateFlow<SyncStatus>

    /** Pede uma rodada assim que possível (não bloqueia; chamada após cada escrita local). */
    fun requestSync()

    /** Tenta agora ("Tentar agora"). */
    suspend fun syncNow(): SyncRunResult
}

/** Conectividade do aparelho (indicador offline). */
interface ConnectivityMonitor {
    val isOnline: StateFlow<Boolean>
}

/** O que o indicador único de sync mostra (ux-spec 4.13). */
sealed interface SyncIndicator {
    data object Synced : SyncIndicator
    data object Syncing : SyncIndicator
    data class Pending(val count: Int) : SyncIndicator
    data class Offline(val pending: Int) : SyncIndicator
    data class Error(val since: Instant, val pending: Int) : SyncIndicator
    data class Rejected(val count: Int) : SyncIndicator
}

fun deriveSyncIndicator(
    online: Boolean,
    engine: SyncStatus,
    pending: Int,
    rejected: Int,
): SyncIndicator = when {
    !online -> SyncIndicator.Offline(pending)
    engine is SyncStatus.Error -> SyncIndicator.Error(engine.since, pending)
    engine is SyncStatus.Syncing -> SyncIndicator.Syncing
    rejected > 0 -> SyncIndicator.Rejected(rejected)
    pending > 0 -> SyncIndicator.Pending(pending)
    else -> SyncIndicator.Synced
}
