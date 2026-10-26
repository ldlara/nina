package app.nina.data.local

import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * Tabela única dos registros de tracking (sono, mamada/mamadeira, pumping, fralda e despertar), distinguidos por
 * [entityType] (`SyncEntityType` do contrato). Instantes em epoch milissegundos (UTC); o fuso do bebê vigente na
 * época fica em [tz] (RB-014). Colunas que não se aplicam ao tipo ficam `NULL`.
 *
 * [deleted] é o **tombstone local**: a linha some das consultas mas permanece até o servidor confirmar o `DELETE`.
 * [version] é a versão canônica do servidor (0 = nunca confirmada).
 */
@Entity(
    tableName = "tracking_event",
    indices = [Index(value = ["babyId", "startAt"]), Index(value = ["sleepSessionId"])],
)
data class EventEntity(
    @PrimaryKey val id: String,
    val babyId: String,
    val entityType: String,
    val version: Int,
    val tz: String,
    /** `start_at`; `occurred_at` (fralda); `started_at` (despertar). */
    val startAt: Long,
    /** `end_at`; `ended_at` (despertar). `NULL` = em andamento. */
    val endAt: Long?,
    val sleepType: String?,
    val sleepSource: String?,
    val methodOrPlace: String?,
    val notes: String?,
    val feedingType: String?,
    val side: String?,
    val volumeMl: Int?,
    val milkType: String?,
    val diaperType: String?,
    val sleepSessionId: String?,
    val wakeSource: String?,
    val lastModifiedByName: String?,
    val createdAt: Long,
    val updatedAt: Long,
    val deleted: Boolean,
    val syncState: String,
)

/**
 * Fila de mutações offline (ADR-0003, RF-046-A3). [seq] define a ordem de envio; [mutationId] (UUID) é único, então
 * reinserir a mesma mutação é ignorado (idempotência). [payload] é o `data` do contrato em JSON.
 */
@Entity(
    tableName = "sync_mutation",
    indices = [
        Index(value = ["mutationId"], unique = true),
        Index(value = ["entityId"]),
        Index(value = ["status", "seq"]),
    ],
)
data class MutationEntity(
    @PrimaryKey(autoGenerate = true) val seq: Long = 0,
    val mutationId: String,
    val babyId: String,
    val entityType: String,
    val entityId: String,
    val op: String,
    val baseVersion: Int,
    val clientCreatedAt: Long,
    val deviceId: String,
    val payload: String?,
    val status: String,
    val attempts: Int,
    val nextAttemptAt: Long,
    val lastErrorCode: String?,
)
