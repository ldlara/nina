package app.nina.domain.tracking

import app.nina.domain.model.BreastSide
import app.nina.domain.model.DiaperType
import app.nina.domain.model.FeedingType
import app.nina.domain.model.MilkType
import app.nina.domain.model.SleepSource
import app.nina.domain.model.SleepType
import app.nina.domain.model.WakeSource
import java.time.Instant

/** Dados de um registro na forma de formulário (ainda não validados). Instantes sempre em UTC. */
sealed interface EventDraft

data class SleepDraft(
    val sleepType: SleepType,
    val startAt: Instant,
    /** `null` = em andamento (timer). */
    val endAt: Instant?,
    val methodOrPlace: String? = null,
    val notes: String? = null,
    val source: SleepSource,
) : EventDraft

/** Mamada: `end_at` é **obrigatório** (ADR-0010, decisão 5) e o lado também (RF-015-A3). */
data class BreastfeedingDraft(
    val side: BreastSide?,
    val startAt: Instant,
    val endAt: Instant?,
    val notes: String? = null,
) : EventDraft

/** Mamadeira: `milk_type` só existe aqui (ADR-0009). */
data class BottleDraft(
    val volumeMl: Int?,
    val milkType: MilkType?,
    val startAt: Instant,
    val endAt: Instant? = null,
    val notes: String? = null,
) : EventDraft

data class PumpingDraft(
    val startAt: Instant,
    val endAt: Instant?,
    val volumeMl: Int? = null,
    val side: BreastSide? = null,
) : EventDraft

data class DiaperDraft(
    val occurredAt: Instant,
    val diaperType: DiaperType?,
    val notes: String? = null,
) : EventDraft

data class WakeDraft(
    val sleepSessionId: String,
    val startedAt: Instant,
    val endedAt: Instant,
    val source: WakeSource = WakeSource.MANUAL,
)

/** Tipo de alimentação a que o rascunho se refere (imutável após criar, contrato `FeedingType`). */
val EventDraft.feedingTypeOrNull: FeedingType?
    get() = when (this) {
        is BreastfeedingDraft -> FeedingType.BREASTFEEDING
        is BottleDraft -> FeedingType.BOTTLE
        else -> null
    }
