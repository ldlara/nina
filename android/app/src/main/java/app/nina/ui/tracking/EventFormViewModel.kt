package app.nina.ui.tracking

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.nina.domain.model.AppError
import app.nina.domain.model.BreastSide
import app.nina.domain.model.DiaperType
import app.nina.domain.model.MilkType
import app.nina.domain.model.Role
import app.nina.domain.model.SleepSource
import app.nina.domain.model.SleepType
import app.nina.domain.repository.BabyRepository
import app.nina.domain.tracking.BottleDraft
import app.nina.domain.tracking.BreastfeedingDraft
import app.nina.domain.tracking.DiaperDraft
import app.nina.domain.tracking.DiaperEvent
import app.nina.domain.tracking.EventDraft
import app.nina.domain.tracking.EventField
import app.nina.domain.tracking.EventIssue
import app.nina.domain.tracking.EventValidator
import app.nina.domain.tracking.FeedingEvent
import app.nina.domain.tracking.PumpingDraft
import app.nina.domain.tracking.PumpingEvent
import app.nina.domain.tracking.QuickDefaults
import app.nina.domain.tracking.Rejection
import app.nina.domain.tracking.SaveResult
import app.nina.domain.tracking.SleepDraft
import app.nina.domain.tracking.SleepEvent
import app.nina.domain.tracking.TrackedEvent
import app.nina.domain.tracking.TrackingLimits
import app.nina.domain.tracking.TrackingRepository
import app.nina.domain.tracking.WakeDraft
import app.nina.domain.tracking.WakeEvent
import app.nina.domain.tracking.inferSleepType
import app.nina.domain.tracking.nightAwakenings
import app.nina.domain.tracking.resolveLocal
import app.nina.domain.model.FeedingType
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.time.Clock
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId

enum class FormKind { SLEEP, BREASTFEEDING, BOTTLE, PUMPING, DIAPER }

data class EventFormState(
    val kind: FormKind,
    val isEdit: Boolean = false,
    val loading: Boolean = true,
    val notFound: Boolean = false,
    val canWrite: Boolean = true,
    val zone: ZoneId = ZoneId.of("UTC"),
    val now: Instant = Instant.EPOCH,
    val start: Instant = Instant.EPOCH,
    /** Fim; em sono `null` = em andamento. */
    val end: Instant? = null,
    val sleepType: SleepType = SleepType.NAP,
    val side: BreastSide? = null,
    val volumeMl: Int? = null,
    val milkType: MilkType? = null,
    val diaperType: DiaperType? = null,
    val methodOrPlace: String = "",
    val notes: String = "",
    val attempted: Boolean = false,
    val issues: List<EventIssue> = emptyList(),
    val overlapWarning: Boolean = false,
    val saving: Boolean = false,
    /** Gravado localmente: a tela pode fechar. */
    val done: Boolean = false,
    val rejection: Rejection? = null,
    val error: AppError? = null,
    val wakes: List<WakeEvent> = emptyList(),
    val nightAwakenings: Int? = null,
) {
    val durationMinutes: Int
        get() = end?.let { Duration.between(start, it).toMinutes().toInt().coerceAtLeast(0) } ?: 0

    fun issue(field: EventField): EventIssue? = if (attempted) issues.firstOrNull { it.field == field } else null
}

/**
 * Formulário único de criação e edição de todos os tipos. Valores padrão inteligentes (agora, último lado, último
 * volume), validação do domínio ([EventValidator]) e gravação offline-first pelo [TrackingRepository].
 */
class EventFormViewModel(
    private val babyId: String,
    private val eventId: String?,
    initialKind: FormKind?,
    private val babies: BabyRepository,
    private val tracking: TrackingRepository,
    private val clock: Clock,
    private val limits: TrackingLimits = TrackingLimits(),
) : ViewModel() {

    private val validator = EventValidator(clock, limits)
    private val _state = MutableStateFlow(EventFormState(kind = initialKind ?: FormKind.SLEEP, isEdit = eventId != null, now = clock.instant()))
    val state: StateFlow<EventFormState> = _state.asStateFlow()

    private var original: TrackedEvent? = null

    init {
        viewModelScope.launch { init(initialKind) }
    }

    private suspend fun init(initialKind: FormKind?) {
        val baby = babies.observeBaby(babyId).first()
        val zone = baby?.let { runCatching { ZoneId.of(it.timezone) }.getOrNull() } ?: ZoneId.of("UTC")
        val canWrite = baby != null && (baby.myRole == Role.OWNER || baby.myRole == Role.CAREGIVER)
        val now = clock.instant()
        if (eventId != null) {
            val ev = tracking.observeEvent(eventId).catch { }.first()
            if (ev == null) {
                _state.update { it.copy(loading = false, notFound = true, zone = zone, canWrite = canWrite) }
                return
            }
            original = ev
            _state.value = fromEvent(ev, zone, now, canWrite)
            if (ev is SleepEvent) {
                viewModelScope.launch {
                    tracking.observeWakeEvents(ev.id).catch { }.collect { wakes ->
                        _state.update { it.copy(wakes = wakes, nightAwakenings = nightAwakenings(ev.copyFrom(it), wakes, limits.nightAwakeningsMinSessionMinutes)) }
                    }
                }
            }
        } else {
            val defaults = tracking.quickDefaults(babyId)
            _state.value = newState(initialKind ?: FormKind.SLEEP, zone, now, defaults, canWrite)
        }
        refreshOverlap()
    }

    private fun SleepEvent.copyFrom(s: EventFormState) = copy(sleepType = s.sleepType, startAt = s.start, endAt = s.end)

    private fun newState(kind: FormKind, zone: ZoneId, now: Instant, d: QuickDefaults, canWrite: Boolean): EventFormState {
        val base = EventFormState(kind = kind, loading = false, canWrite = canWrite, zone = zone, now = now, start = now)
        return when (kind) {
            FormKind.SLEEP -> {
                val start = now.minus(Duration.ofHours(1))
                base.copy(start = start, end = now, sleepType = inferSleepType(start, zone))
            }
            FormKind.BREASTFEEDING -> {
                val minutes = (d.lastBreastMinutes ?: 10).coerceIn(1, maxMinutes())
                base.copy(start = now.minus(Duration.ofMinutes(minutes.toLong())), end = now, side = d.lastBreastSide)
            }
            FormKind.BOTTLE -> base.copy(volumeMl = d.lastBottleVolumeMl ?: DEFAULT_BOTTLE_ML, milkType = d.lastMilkType)
            FormKind.PUMPING -> {
                val minutes = (d.lastPumpingMinutes ?: 15).coerceIn(1, maxMinutes())
                base.copy(start = now.minus(Duration.ofMinutes(minutes.toLong())), end = now, side = d.lastPumpingSide, volumeMl = d.lastPumpingVolumeMl)
            }
            FormKind.DIAPER -> base
        }
    }

    private fun maxMinutes() = limits.maxFeedingDuration.toMinutes().toInt()

    private fun fromEvent(e: TrackedEvent, zone: ZoneId, now: Instant, canWrite: Boolean): EventFormState {
        val base = EventFormState(kind = FormKind.SLEEP, isEdit = true, loading = false, canWrite = canWrite, zone = zone, now = now)
        return when (e) {
            is SleepEvent -> base.copy(
                kind = FormKind.SLEEP, start = e.startAt, end = e.endAt, sleepType = e.sleepType,
                methodOrPlace = e.methodOrPlace.orEmpty(), notes = e.notes.orEmpty(),
            )
            is FeedingEvent -> base.copy(
                kind = if (e.feedingType == FeedingType.BOTTLE) FormKind.BOTTLE else FormKind.BREASTFEEDING,
                start = e.startAt, end = e.endAt, side = e.side, volumeMl = e.volumeMl, milkType = e.milkType, notes = e.notes.orEmpty(),
            )
            is PumpingEvent -> base.copy(kind = FormKind.PUMPING, start = e.startAt, end = e.endAt, volumeMl = e.volumeMl, side = e.side)
            is DiaperEvent -> base.copy(kind = FormKind.DIAPER, start = e.occurredAt, diaperType = e.diaperType, notes = e.notes.orEmpty())
        }
    }

    // ---- edição de campos ------------------------------------------------------------------------------------------
    fun setSleepType(t: SleepType) = change { it.copy(sleepType = t) }
    fun setSide(s: BreastSide) = change { it.copy(side = s) }
    fun setMilkType(m: MilkType?) = change { it.copy(milkType = m) }
    fun setDiaperType(t: DiaperType) = change { it.copy(diaperType = t) }
    fun setMethod(v: String) = change { it.copy(methodOrPlace = v) }
    fun setNotes(v: String) = change { it.copy(notes = v) }
    fun setVolume(ml: Int?) = change { it.copy(volumeMl = ml) }

    /** Stepper de volume (±10 ml). Abaixo de 1 ml: campo opcional volta a "sem volume"; obrigatório fica em 1. */
    fun adjustVolume(delta: Int, max: Int = limits.bottleVolumeMlMax, optional: Boolean = false) = change { s ->
        val next = (s.volumeMl ?: 0) + delta
        s.copy(volumeMl = if (next <= 0) (if (optional) null else 1) else next.coerceAtMost(max))
    }

    /** Atalhos "-5, -10, -15, -30": o horário principal passa a ser "agora" menos N minutos. */
    fun setMinutesAgo(minutes: Int) = change { s ->
        val t = clock.instant().minus(Duration.ofMinutes(minutes.toLong()))
        when (s.kind) {
            FormKind.BREASTFEEDING, FormKind.PUMPING -> s.withEnd(t)
            FormKind.SLEEP -> if (s.end != null) s.copy(end = t.takeIf { it.isAfter(s.start) } ?: s.end) else s.copy(start = t)
            else -> s.copy(start = t)
        }
    }

    /** Define data+hora de **início** (ou o horário único, em mamadeira/fralda) no fuso do bebê. */
    fun setStart(date: LocalDate, time: LocalTime) = change { s ->
        val t = resolveLocal(date, time, s.zone)
        when (s.kind) {
            FormKind.BREASTFEEDING, FormKind.PUMPING -> s.copy(start = t, end = s.end?.let { e -> if (e.isBefore(t)) t else e })
            else -> s.copy(start = t)
        }
    }

    /** Define data+hora de **fim**. Em mamada/bomba mantém a duração (desloca o início). */
    fun setEnd(date: LocalDate, time: LocalTime) = change { s ->
        val t = resolveLocal(date, time, s.zone)
        if (s.kind == FormKind.BREASTFEEDING || s.kind == FormKind.PUMPING) s.withEnd(t) else s.copy(end = t)
    }

    fun setDurationMinutes(minutes: Int) = change { s ->
        val m = minutes.coerceIn(1, maxMinutes())
        val end = s.end ?: s.now
        s.copy(start = end.minus(Duration.ofMinutes(m.toLong())), end = end)
    }

    /** Sono em andamento (sem fim): só existe ao editar um timer aberto. */
    fun setOngoing(ongoing: Boolean) = change { s -> s.copy(end = if (ongoing) null else (s.end ?: s.now.takeIf { it.isAfter(s.start) } ?: s.start.plusSeconds(60))) }

    private fun EventFormState.withEnd(newEnd: Instant): EventFormState {
        val duration = Duration.between(start, end ?: start)
        return copy(end = newEnd, start = newEnd.minus(duration))
    }

    private inline fun change(block: (EventFormState) -> EventFormState) {
        _state.update { s ->
            var n = block(s)
            // Mamadeira recebida do servidor pode ter `end_at`: ao mover o horário, o fim acompanha (mesma duração).
            if (n.kind == FormKind.BOTTLE && s.end != null && n.start != s.start) {
                n = n.copy(end = s.end.plus(Duration.between(s.start, n.start)))
            }
            n.copy(issues = validator.validate(n.toDraft()), rejection = null, error = null)
        }
        refreshOverlap()
    }

    private fun refreshOverlap() {
        val s = _state.value
        if (s.kind != FormKind.SLEEP || s.loading) return
        viewModelScope.launch {
            val overlap = tracking.hasSleepOverlap(babyId, s.start, s.end, eventId)
            _state.update { it.copy(overlapWarning = overlap) }
        }
    }

    // ---- salvar / excluir --------------------------------------------------------------------------------------------
    fun save() {
        val s = _state.value
        if (s.saving || !s.canWrite) return
        val draft = s.toDraft()
        val issues = validator.validate(draft)
        if (issues.isNotEmpty()) {
            _state.update { it.copy(attempted = true, issues = issues) }
            return
        }
        _state.update { it.copy(saving = true, attempted = true, issues = emptyList(), error = null, rejection = null) }
        viewModelScope.launch {
            val result = if (eventId == null) tracking.create(babyId, draft) else tracking.update(eventId, draft)
            when (result) {
                is SaveResult.Saved -> _state.update { it.copy(saving = false, done = true) }
                is SaveResult.Invalid -> _state.update { it.copy(saving = false, issues = result.issues) }
                is SaveResult.Rejected -> _state.update { it.copy(saving = false, rejection = result.reason) }
                is SaveResult.Failed -> _state.update { it.copy(saving = false, error = result.error) }
            }
        }
    }

    /** Exclusão (a UI pede confirmação antes; RF-020-A6). */
    fun delete() {
        val id = eventId ?: return
        viewModelScope.launch {
            when (val r = tracking.delete(id)) {
                is app.nina.domain.model.Outcome.Success -> _state.update { it.copy(done = true) }
                is app.nina.domain.model.Outcome.Failure -> _state.update { it.copy(error = r.error) }
            }
        }
    }

    // ---- despertares (WakeEvent) ----------------------------------------------------------------------------------------
    fun addWake(startedAt: Instant, endedAt: Instant) {
        val sleep = (original as? SleepEvent) ?: return
        viewModelScope.launch {
            when (val r = tracking.createWake(babyId, WakeDraft(sleep.id, startedAt, endedAt))) {
                is SaveResult.Saved -> Unit
                is SaveResult.Invalid -> _state.update { it.copy(issues = r.issues, attempted = true) }
                is SaveResult.Rejected -> _state.update { it.copy(rejection = r.reason) }
                is SaveResult.Failed -> _state.update { it.copy(error = r.error) }
            }
        }
    }

    fun removeWake(id: String) {
        viewModelScope.launch { tracking.deleteWake(id) }
    }

    fun clearRejection() = _state.update { it.copy(rejection = null, error = null) }
}

internal fun EventFormState.toDraft(): EventDraft = when (kind) {
    FormKind.SLEEP -> SleepDraft(
        sleepType = sleepType, startAt = start, endAt = end,
        methodOrPlace = methodOrPlace.ifBlank { null }, notes = notes.ifBlank { null }, source = SleepSource.MANUAL,
    )
    FormKind.BREASTFEEDING -> BreastfeedingDraft(side, start, end, notes.ifBlank { null })
    FormKind.BOTTLE -> BottleDraft(volumeMl, milkType, start, end, notes.ifBlank { null })
    FormKind.PUMPING -> PumpingDraft(start, end, volumeMl, side)
    FormKind.DIAPER -> DiaperDraft(start, diaperType, notes.ifBlank { null })
}

private const val DEFAULT_BOTTLE_ML = 90
