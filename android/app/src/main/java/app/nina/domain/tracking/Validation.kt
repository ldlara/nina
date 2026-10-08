package app.nina.domain.tracking

import java.time.Clock
import java.time.Duration
import java.time.Instant

/**
 * Limites de validação local. Os padrões seguem o contrato (`notes` 500, `method_or_place` 80, ml inteiro >= 1) e o
 * exemplo de `/reference-data` (`bottle_volume_ml_max` 500). O servidor é a autoridade: o limite efetivo pode ser maior
 * (D-22 em aberto). Ver README, "Desvios".
 */
data class TrackingLimits(
    val bottleVolumeMlMax: Int = 500,
    val pumpingVolumeMlMax: Int = 500,
    val notesMaxLength: Int = 500,
    val methodMaxLength: Int = 80,
    val maxSleepDuration: Duration = Duration.ofHours(24),
    val maxFeedingDuration: Duration = Duration.ofHours(6),
    /** Tolerância para relógio adiantado antes de tratar um horário como "no futuro". */
    val futureTolerance: Duration = Duration.ofMinutes(5),
    /** `limits.night_awakenings_min_session_minutes` (padrão 240). */
    val nightAwakeningsMinSessionMinutes: Int = 240,
    /** Após este tempo em andamento, pergunta-se "O bebê ainda está dormindo?" (ux-spec 4.2). */
    val longSleepPrompt: Duration = Duration.ofHours(6),
)

enum class EventField { START, END, DURATION, SIDE, VOLUME, MILK_TYPE, DIAPER_TYPE, SLEEP_TYPE, NOTES, METHOD }

enum class IssueCode { REQUIRED, END_BEFORE_START, FUTURE_TIME, TOO_LONG_SESSION, VOLUME_RANGE, TEXT_TOO_LONG, NOT_APPLICABLE, INVALID }

data class EventIssue(val field: EventField, val code: IssueCode)

/** Regras de RF-008/015/016/017/018 e do contrato (`SleepData`, `BreastFeedingData`, `BottleFeedingData`...). */
class EventValidator(
    private val clock: Clock,
    private val limits: TrackingLimits = TrackingLimits(),
) {
    fun validate(draft: EventDraft): List<EventIssue> = buildList {
        when (draft) {
            is SleepDraft -> {
                if (draft.sleepType == app.nina.domain.model.SleepType.UNRECOGNIZED) add(EventIssue(EventField.SLEEP_TYPE, IssueCode.REQUIRED))
                checkStart(draft.startAt)
                draft.endAt?.let { checkEnd(draft.startAt, it, limits.maxSleepDuration) }
                checkText(draft.methodOrPlace, limits.methodMaxLength, EventField.METHOD)
                checkText(draft.notes, limits.notesMaxLength, EventField.NOTES)
            }
            is BreastfeedingDraft -> {
                if (draft.side == null || draft.side == app.nina.domain.model.BreastSide.UNRECOGNIZED) {
                    add(EventIssue(EventField.SIDE, IssueCode.REQUIRED))
                }
                checkStart(draft.startAt)
                // end_at obrigatório na mamada (ADR-0010).
                if (draft.endAt == null) add(EventIssue(EventField.END, IssueCode.REQUIRED))
                else checkEnd(draft.startAt, draft.endAt, limits.maxFeedingDuration)
                checkText(draft.notes, limits.notesMaxLength, EventField.NOTES)
            }
            is BottleDraft -> {
                val v = draft.volumeMl
                if (v == null) add(EventIssue(EventField.VOLUME, IssueCode.REQUIRED))
                else if (v < 1 || v > limits.bottleVolumeMlMax) add(EventIssue(EventField.VOLUME, IssueCode.VOLUME_RANGE))
                if (draft.milkType == app.nina.domain.model.MilkType.UNRECOGNIZED) add(EventIssue(EventField.MILK_TYPE, IssueCode.INVALID))
                checkStart(draft.startAt)
                draft.endAt?.let { checkEnd(draft.startAt, it, limits.maxFeedingDuration) }
                checkText(draft.notes, limits.notesMaxLength, EventField.NOTES)
            }
            is PumpingDraft -> {
                checkStart(draft.startAt)
                if (draft.endAt == null) add(EventIssue(EventField.END, IssueCode.REQUIRED))
                else checkEnd(draft.startAt, draft.endAt, limits.maxFeedingDuration)
                draft.volumeMl?.let { if (it < 1 || it > limits.pumpingVolumeMlMax) add(EventIssue(EventField.VOLUME, IssueCode.VOLUME_RANGE)) }
                if (draft.side == app.nina.domain.model.BreastSide.UNRECOGNIZED) add(EventIssue(EventField.SIDE, IssueCode.INVALID))
            }
            is DiaperDraft -> {
                if (draft.diaperType == null || draft.diaperType == app.nina.domain.model.DiaperType.UNRECOGNIZED) {
                    add(EventIssue(EventField.DIAPER_TYPE, IssueCode.REQUIRED))
                }
                checkStart(draft.occurredAt)
                checkText(draft.notes, limits.notesMaxLength, EventField.NOTES)
            }
        }
    }

    fun validate(draft: WakeDraft, session: SleepEvent?): List<EventIssue> = buildList {
        if (!draft.endedAt.isAfter(draft.startedAt)) add(EventIssue(EventField.END, IssueCode.END_BEFORE_START))
        if (session == null) add(EventIssue(EventField.START, IssueCode.INVALID))
        else {
            if (draft.startedAt.isBefore(session.startAt)) add(EventIssue(EventField.START, IssueCode.INVALID))
            val sessionEnd = session.endAt
            if (sessionEnd != null && draft.endedAt.isAfter(sessionEnd)) add(EventIssue(EventField.END, IssueCode.INVALID))
        }
        if (draft.endedAt.isAfter(clock.instant().plus(limits.futureTolerance))) add(EventIssue(EventField.END, IssueCode.FUTURE_TIME))
    }

    private fun MutableList<EventIssue>.checkStart(start: Instant) {
        if (start.isAfter(clock.instant().plus(limits.futureTolerance))) add(EventIssue(EventField.START, IssueCode.FUTURE_TIME))
    }

    private fun MutableList<EventIssue>.checkEnd(start: Instant, end: Instant, max: Duration) {
        when {
            end.isBefore(start) -> add(EventIssue(EventField.END, IssueCode.END_BEFORE_START))
            Duration.between(start, end) > max -> add(EventIssue(EventField.END, IssueCode.TOO_LONG_SESSION))
            end.isAfter(clock.instant().plus(limits.futureTolerance)) -> add(EventIssue(EventField.END, IssueCode.FUTURE_TIME))
        }
    }

    private fun MutableList<EventIssue>.checkText(text: String?, max: Int, field: EventField) {
        if (text != null && text.length > max) add(EventIssue(field, IssueCode.TEXT_TOO_LONG))
    }
}
