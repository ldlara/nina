package app.nina.data.local

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import kotlinx.coroutines.flow.Flow

private const val NOT_WAKE = "entityType != 'WAKE_EVENT' AND deleted = 0"

@Dao
interface TrackingDao {
    // ---- leitura da timeline (sempre local) ------------------------------------------------------------------
    /** Sono em andamento primeiro; depois `start_at` desc com desempate por `id` (contrato, `/timeline`). */
    @Query(
        "SELECT * FROM tracking_event WHERE babyId = :babyId AND $NOT_WAKE " +
            "ORDER BY CASE WHEN endAt IS NULL AND entityType = 'SLEEP_SESSION' THEN 0 ELSE 1 END, startAt DESC, id DESC LIMIT :limit",
    )
    fun observeTimeline(babyId: String, limit: Int): Flow<List<EventEntity>>

    @Query(
        "SELECT * FROM tracking_event WHERE babyId = :babyId AND $NOT_WAKE " +
            "AND startAt < :to AND COALESCE(endAt, startAt) >= :from ORDER BY startAt DESC, id DESC",
    )
    fun observeRange(babyId: String, from: Long, to: Long): Flow<List<EventEntity>>

    @Query("SELECT * FROM tracking_event WHERE id = :id AND deleted = 0 AND entityType != 'WAKE_EVENT'")
    fun observeEvent(id: String): Flow<EventEntity?>

    @Query(
        "SELECT * FROM tracking_event WHERE babyId = :babyId AND entityType = 'SLEEP_SESSION' AND deleted = 0 " +
            "AND endAt IS NULL ORDER BY startAt DESC LIMIT 1",
    )
    fun observeOpenSleep(babyId: String): Flow<EventEntity?>

    @Query(
        "SELECT * FROM tracking_event WHERE babyId = :babyId AND entityType = 'FEEDING_SESSION' AND deleted = 0 " +
            "AND feedingType IN ('BREASTFEEDING','BOTTLE') ORDER BY startAt DESC LIMIT 1",
    )
    fun observeLastFeeding(babyId: String): Flow<EventEntity?>

    @Query(
        "SELECT * FROM tracking_event WHERE babyId = :babyId AND entityType = 'SLEEP_SESSION' AND deleted = 0 " +
            "AND endAt IS NOT NULL ORDER BY endAt DESC LIMIT 1",
    )
    fun observeLastClosedSleep(babyId: String): Flow<EventEntity?>

    @Query("SELECT * FROM tracking_event WHERE sleepSessionId = :sessionId AND entityType = 'WAKE_EVENT' AND deleted = 0 ORDER BY startAt ASC")
    fun observeWakeEvents(sessionId: String): Flow<List<EventEntity>>

    // ---- leitura pontual ------------------------------------------------------------------------------------
    @Query("SELECT * FROM tracking_event WHERE id = :id")
    suspend fun get(id: String): EventEntity?

    @Query(
        "SELECT * FROM tracking_event WHERE babyId = :babyId AND entityType = :entityType AND deleted = 0 " +
            "AND (:feedingType IS NULL OR feedingType = :feedingType) ORDER BY startAt DESC LIMIT 1",
    )
    suspend fun lastOfType(babyId: String, entityType: String, feedingType: String?): EventEntity?

    @Query(
        "SELECT * FROM tracking_event WHERE babyId = :babyId AND entityType = 'SLEEP_SESSION' AND deleted = 0 " +
            "AND endAt IS NULL ORDER BY startAt DESC LIMIT 1",
    )
    suspend fun getOpenSleep(babyId: String): EventEntity?

    @Query(
        "SELECT COUNT(*) FROM tracking_event WHERE babyId = :babyId AND entityType = 'SLEEP_SESSION' AND deleted = 0 " +
            "AND id != :excludeId AND startAt < :end AND COALESCE(endAt, 9223372036854775807) > :start",
    )
    suspend fun countSleepOverlap(babyId: String, start: Long, end: Long, excludeId: String): Int

    @Query("SELECT * FROM tracking_event WHERE sleepSessionId = :sessionId AND entityType = 'WAKE_EVENT'")
    suspend fun wakeEventsOf(sessionId: String): List<EventEntity>

    // ---- escrita ---------------------------------------------------------------------------------------------
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(event: EventEntity)

    @Query("DELETE FROM tracking_event WHERE id = :id")
    suspend fun hardDelete(id: String)

    @Query("UPDATE tracking_event SET deleted = 1, syncState = 'PENDING', updatedAt = :now WHERE id = :id")
    suspend fun markDeleted(id: String, now: Long)

    @Query("UPDATE tracking_event SET version = :version, syncState = :syncState WHERE id = :id")
    suspend fun setVersionAndState(id: String, version: Int, syncState: String)

    @Query("UPDATE tracking_event SET syncState = :syncState WHERE id = :id")
    suspend fun setSyncState(id: String, syncState: String)

    @Query("DELETE FROM tracking_event")
    suspend fun deleteAll()
}

@Dao
interface MutationDao {
    /** Devolve `-1` se o `mutationId` já existe (idempotência por UUID). */
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insert(mutation: MutationEntity): Long

    @Query("SELECT COUNT(*) FROM sync_mutation WHERE status IN ('PENDING','IN_FLIGHT','RETRY')")
    fun observePendingCount(): Flow<Int>

    @Query("SELECT COUNT(*) FROM sync_mutation WHERE status = 'REJECTED'")
    fun observeRejectedCount(): Flow<Int>

    @Query("SELECT * FROM sync_mutation WHERE status IN ('PENDING','RETRY') ORDER BY seq ASC LIMIT :limit")
    suspend fun sendable(limit: Int): List<MutationEntity>

    /** Mutações em voo: enquanto houver, nenhum novo lote sai (mantém a ordem e evita envio duplicado). */
    @Query("SELECT * FROM sync_mutation WHERE status = 'IN_FLIGHT' ORDER BY seq ASC")
    suspend fun inFlight(): List<MutationEntity>

    @Query("SELECT * FROM sync_mutation WHERE mutationId = :mutationId")
    suspend fun get(mutationId: String): MutationEntity?

    @Query("SELECT * FROM sync_mutation ORDER BY seq ASC")
    suspend fun all(): List<MutationEntity>

    @Query("SELECT * FROM sync_mutation WHERE entityId = :entityId ORDER BY seq ASC")
    suspend fun forEntity(entityId: String): List<MutationEntity>

    /**
     * CREATE que o servidor ainda não aceitou (pendente, em retry ou recusada) e que não está em voo: pode absorver
     * edições e exclusão sem gerar novas mutações.
     */
    @Query("SELECT * FROM sync_mutation WHERE entityId = :entityId AND op = 'CREATE' AND status IN ('PENDING','RETRY','REJECTED') LIMIT 1")
    suspend fun unsentCreate(entityId: String): MutationEntity?

    /** Reescreve o `data` de uma CREATE ainda não enviada; reabre-a para envio (corrige uma recusa). */
    @Query("UPDATE sync_mutation SET payload = :payload, status = 'PENDING', attempts = 0, nextAttemptAt = 0, lastErrorCode = NULL WHERE mutationId = :mutationId")
    suspend fun rewriteCreate(mutationId: String, payload: String?)

    @Query("UPDATE sync_mutation SET status = 'IN_FLIGHT' WHERE mutationId IN (:ids) AND status IN ('PENDING','RETRY')")
    suspend fun markInFlight(ids: List<String>)

    @Query("DELETE FROM sync_mutation WHERE mutationId = :mutationId")
    suspend fun delete(mutationId: String)

    @Query("DELETE FROM sync_mutation WHERE entityId = :entityId")
    suspend fun deleteForEntity(entityId: String)

    @Query("SELECT COUNT(*) FROM sync_mutation WHERE entityId = :entityId AND status != 'REJECTED'")
    suspend fun countActiveForEntity(entityId: String): Int

    @Query(
        "UPDATE sync_mutation SET status = 'RETRY', attempts = attempts + 1, nextAttemptAt = :next, lastErrorCode = :code " +
            "WHERE mutationId = :mutationId",
    )
    suspend fun markRetry(mutationId: String, code: String, next: Long)

    @Query("UPDATE sync_mutation SET status = 'REJECTED', lastErrorCode = :code WHERE mutationId = :mutationId")
    suspend fun markRejected(mutationId: String, code: String)

    /** O servidor deu uma versão nova ao registro: mutações ainda não enviadas dele partem dessa base. */
    @Query("UPDATE sync_mutation SET baseVersion = :version WHERE entityId = :entityId AND baseVersion < :version AND status != 'IN_FLIGHT' AND op != 'CREATE'")
    suspend fun rebase(entityId: String, version: Int)

    @Query("UPDATE sync_mutation SET status = 'PENDING' WHERE status = 'IN_FLIGHT'")
    suspend fun releaseInFlight()

    @Query("UPDATE sync_mutation SET status = 'PENDING', nextAttemptAt = 0 WHERE status = 'RETRY'")
    suspend fun retryNow()

    @Query("DELETE FROM sync_mutation")
    suspend fun deleteAll()
}
