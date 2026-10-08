package app.nina.domain.tracking

import app.nina.domain.model.BreastSide
import app.nina.domain.model.DiaperType
import app.nina.domain.model.FeedingType
import app.nina.domain.model.MilkType
import app.nina.domain.model.SleepSource
import app.nina.domain.model.SleepType
import app.nina.domain.model.WakeSource
import java.time.Duration
import java.time.Instant

/**
 * Estado de sincronização de um registro local (RF-046-A6). Derivado da fila de mutações, nunca "verdade" do evento:
 * `PENDING` = há mutação aguardando envio; `FAILED` = o servidor recusou sem possibilidade de reenvio; `SYNCED` = nada pendente.
 */
enum class SyncState { PENDING, SYNCED, FAILED }

/** Evento real exibido na timeline. Previsões nunca são eventos (RB-001). */
sealed interface TrackedEvent {
    val id: String
    val babyId: String

    /** Versão canônica do servidor; `0` = ainda nunca confirmado. */
    val version: Int
    val tz: String
    val syncState: SyncState
    val updatedAt: Instant

    /** Nome de quem alterou por último, quando o servidor informou (vários cuidadores, RF-019-A5). */
    val lastModifiedByName: String?

    /** Instante usado para ordenar e agrupar por dia (início; `occurred_at` na fralda). */
    val startInstant: Instant
}

data class SleepEvent(
    override val id: String,
    override val babyId: String,
    override val version: Int,
    override val tz: String,
    override val syncState: SyncState,
    override val updatedAt: Instant,
    override val lastModifiedByName: String?,
    val sleepType: SleepType,
    val startAt: Instant,
    /** `null` = em andamento. */
    val endAt: Instant?,
    val methodOrPlace: String?,
    val notes: String?,
    val source: SleepSource,
) : TrackedEvent {
    override val startInstant: Instant get() = startAt
    val isOpen: Boolean get() = endAt == null
    val duration: Duration? get() = endAt?.let { Duration.between(startAt, it) }
}

data class FeedingEvent(
    override val id: String,
    override val babyId: String,
    override val version: Int,
    override val tz: String,
    override val syncState: SyncState,
    override val updatedAt: Instant,
    override val lastModifiedByName: String?,
    val feedingType: FeedingType,
    val startAt: Instant,
    val endAt: Instant?,
    /** Só em `BREASTFEEDING`; nos demais `null` (não se aplica). */
    val side: BreastSide?,
    /** Só em `BOTTLE`. */
    val volumeMl: Int?,
    /** Só em `BOTTLE`; `null` = não informado ou não se aplica. */
    val milkType: MilkType?,
    val notes: String?,
) : TrackedEvent {
    override val startInstant: Instant get() = startAt
    val duration: Duration? get() = endAt?.let { Duration.between(startAt, it) }
}

data class PumpingEvent(
    override val id: String,
    override val babyId: String,
    override val version: Int,
    override val tz: String,
    override val syncState: SyncState,
    override val updatedAt: Instant,
    override val lastModifiedByName: String?,
    val startAt: Instant,
    val endAt: Instant,
    val volumeMl: Int?,
    val side: BreastSide?,
) : TrackedEvent {
    override val startInstant: Instant get() = startAt
    val duration: Duration get() = Duration.between(startAt, endAt)
}

data class DiaperEvent(
    override val id: String,
    override val babyId: String,
    override val version: Int,
    override val tz: String,
    override val syncState: SyncState,
    override val updatedAt: Instant,
    override val lastModifiedByName: String?,
    val occurredAt: Instant,
    val diaperType: DiaperType,
    val notes: String?,
) : TrackedEvent {
    override val startInstant: Instant get() = occurredAt
}

/** Despertar durante uma sessão de sono (fonte da verdade de `night_awakenings`, ADR-0009). Não aparece na timeline. */
data class WakeEvent(
    val id: String,
    val babyId: String,
    val version: Int,
    val syncState: SyncState,
    val sleepSessionId: String,
    val startedAt: Instant,
    val endedAt: Instant,
    val source: WakeSource,
) {
    val duration: Duration get() = Duration.between(startedAt, endedAt)
}

/** Valores sugeridos nos formulários (último lado, último volume...), "valores padrão inteligentes" (ux-spec 2). */
data class QuickDefaults(
    val lastBreastSide: BreastSide? = null,
    val lastBreastMinutes: Int? = null,
    val lastBottleVolumeMl: Int? = null,
    val lastMilkType: MilkType? = null,
    val lastPumpingSide: BreastSide? = null,
    val lastPumpingVolumeMl: Int? = null,
    val lastPumpingMinutes: Int? = null,
)
