package app.nina.ui.today

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.nina.domain.model.AppError
import app.nina.domain.model.Baby
import app.nina.domain.model.DiaperType
import app.nina.domain.model.Role
import app.nina.domain.model.SleepSource
import app.nina.domain.repository.BabyRepository
import app.nina.domain.tracking.DaySummary
import app.nina.domain.tracking.DayWindow
import app.nina.domain.tracking.DiaperDraft
import app.nina.domain.tracking.Rejection
import app.nina.domain.tracking.SaveResult
import app.nina.domain.tracking.SleepDraft
import app.nina.domain.tracking.SleepEvent
import app.nina.domain.tracking.SyncEngine
import app.nina.domain.tracking.SyncIndicator
import app.nina.domain.tracking.SyncRunResult
import app.nina.domain.tracking.TrackedEvent
import app.nina.domain.tracking.TrackingLimits
import app.nina.domain.tracking.TrackingRepository
import app.nina.domain.tracking.inferSleepType
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.time.Clock
import java.time.Duration
import java.time.Instant
import java.time.ZoneId

/** Mensagem efêmera (snackbar). A UI localiza; "Desfazer" fica 8 s (ux-spec 2, princípio 7). */
sealed interface TodayMessage {
    data class DiaperSaved(val eventId: String) : TodayMessage
    data object SleepStarted : TodayMessage
    data object OpenSleepExists : TodayMessage
    data object ReadOnly : TodayMessage
    data object SyncNotAvailable : TodayMessage
    data object SyncOffline : TodayMessage
    data class Failed(val error: AppError?) : TodayMessage
}

data class TodayUiState(
    val loading: Boolean = true,
    val baby: Baby? = null,
    val zone: ZoneId = ZoneId.of("UTC"),
    val now: Instant = Instant.EPOCH,
    val openSleep: SleepEvent? = null,
    val lastClosedSleep: SleepEvent? = null,
    val lastFeedingAt: Instant? = null,
    val summary: DaySummary = DaySummary.EMPTY,
    /** Nenhum registro hoje (e nenhum sono em andamento): a Home mostra orientação (ux-spec 4.12). */
    val noRecordsToday: Boolean = true,
    val sync: SyncIndicator = SyncIndicator.Synced,
    val canRecord: Boolean = false,
    /** Sono em andamento há muito tempo: pergunta discreta "O bebê ainda está dormindo?" (ux-spec 4.2). */
    val longSleepPrompt: Boolean = false,
    /** Sono recém-finalizado: sheet de confirmação com ajuste do fim, detalhes e desfazer. */
    val justStopped: SleepEvent? = null,
    val message: TodayMessage? = null,
    val error: AppError? = null,
)

class TodayViewModel(
    private val babyId: String,
    private val babies: BabyRepository,
    private val tracking: TrackingRepository,
    syncIndicator: Flow<SyncIndicator>,
    private val syncEngine: SyncEngine,
    private val clock: Clock,
    private val limits: TrackingLimits = TrackingLimits(),
) : ViewModel() {

    private val _state = MutableStateFlow(TodayUiState(now = clock.instant()))
    val state: StateFlow<TodayUiState> = _state.asStateFlow()

    private val nowFlow = MutableStateFlow(clock.instant())
    private var longSleepDismissedFor: String? = null

    init {
        observe(syncIndicator)
    }

    @OptIn(ExperimentalCoroutinesApi::class)
    private fun observe(syncIndicator: Flow<SyncIndicator>) {
        viewModelScope.launch {
            babies.observeBaby(babyId).collect { baby ->
                _state.update {
                    it.copy(
                        baby = baby,
                        loading = false,
                        zone = baby?.let { b -> runCatching { ZoneId.of(b.timezone) }.getOrNull() } ?: it.zone,
                        canRecord = baby != null && (baby.myRole == Role.OWNER || baby.myRole == Role.CAREGIVER),
                    )
                }
            }
        }
        viewModelScope.launch {
            tracking.observeOpenSleep(babyId).catch { fail(it) }.collect { open ->
                _state.update { it.copy(openSleep = open) }
                recomputeLongSleep()
            }
        }
        viewModelScope.launch { tracking.observeLastClosedSleep(babyId).catch { fail(it) }.collect { s -> _state.update { it.copy(lastClosedSleep = s) } } }
        viewModelScope.launch {
            tracking.observeLastFeeding(babyId).catch { fail(it) }.collect { f -> _state.update { it.copy(lastFeedingAt = f?.startAt) } }
        }
        viewModelScope.launch { syncIndicator.catch { }.collect { s -> _state.update { it.copy(sync = s) } } }
        // Resumo do dia: depende do fuso do bebê e do "agora" (virada de dia, DST).
        viewModelScope.launch {
            combine(_state.map { it.zone }.distinctUntilChanged(), nowFlow.map { it }) { zone, now -> DayWindow.containing(now, zone) }
                .distinctUntilChanged()
                .flatMapLatest { w -> tracking.observeRange(babyId, w.start, w.end).map { events -> w to events } }
                .catch { fail(it) }
                .collect { (w, events) ->
                    _state.update {
                        it.copy(
                            summary = DaySummary.of(events, w),
                            noRecordsToday = events.isEmpty(),
                        )
                    }
                }
        }
    }

    private fun fail(t: Throwable) {
        _state.update { it.copy(error = AppError.Unexpected(t.message), loading = false) }
    }

    /** A UI chama periodicamente (a cada minuto) e ao voltar ao primeiro plano. */
    fun tick() {
        val now = clock.instant()
        nowFlow.value = now
        _state.update { it.copy(now = now) }
        recomputeLongSleep()
    }

    private fun recomputeLongSleep() {
        _state.update { s ->
            val open = s.openSleep
            val prompt = open != null && open.id != longSleepDismissedFor &&
                Duration.between(open.startAt, s.now) >= limits.longSleepPrompt
            s.copy(longSleepPrompt = prompt)
        }
    }

    fun dismissLongSleepPrompt() {
        longSleepDismissedFor = _state.value.openSleep?.id
        recomputeLongSleep()
    }

    // ---- sono: timer ---------------------------------------------------------------------------------------------
    /** "Dormiu" (1 toque). [minutesAgo] > 0 = "Foi antes…" (hora de início retroativa). */
    fun startSleep(minutesAgo: Int = 0) {
        val s = _state.value
        val start = clock.instant().minus(Duration.ofMinutes(minutesAgo.toLong()))
        viewModelScope.launch {
            val draft = SleepDraft(inferSleepType(start, s.zone), start, null, source = SleepSource.TIMER)
            handle(tracking.create(babyId, draft)) { _state.update { it.copy(message = TodayMessage.SleepStarted) } }
        }
    }

    /** Corrige o início do timer para mais cedo ("Foi antes?": -5, -10, -15, -30). */
    fun moveSleepStartEarlier(minutes: Int) {
        val open = _state.value.openSleep ?: return
        val newStart = open.startAt.minus(Duration.ofMinutes(minutes.toLong()))
        viewModelScope.launch { handle(tracking.update(open.id, open.toDraft().copy(startAt = newStart))) {} }
    }

    fun setSleepType(type: app.nina.domain.model.SleepType) {
        val open = _state.value.openSleep ?: return
        viewModelScope.launch { handle(tracking.update(open.id, open.toDraft().copy(sleepType = type))) {} }
    }

    /** "Acordou" (1 toque): grava o fim agora e abre a confirmação leve. [minutesAgo] = "Foi antes…" do fim. */
    fun stopSleep(minutesAgo: Int = 0) {
        val open = _state.value.openSleep ?: return
        val now = clock.instant()
        val requested = now.minus(Duration.ofMinutes(minutesAgo.toLong()))
        // O fim nunca fica antes (nem igual) ao início.
        val end = listOf(requested, now).firstOrNull { it.isAfter(open.startAt) } ?: open.startAt.plusSeconds(1)
        viewModelScope.launch {
            handle(tracking.update(open.id, open.toDraft().copy(endAt = end))) { saved ->
                _state.update { it.copy(justStopped = saved as? SleepEvent) }
            }
        }
    }

    /** Ajusta o fim já gravado ("Ajustar fim: -5 -10 ..."). */
    fun adjustStoppedEnd(minutesEarlier: Int) {
        val stopped = _state.value.justStopped ?: return
        val end = stopped.endAt ?: return
        val newEnd = end.minus(Duration.ofMinutes(minutesEarlier.toLong()))
        if (!newEnd.isAfter(stopped.startAt)) {
            _state.update { it.copy(message = TodayMessage.Failed(null)) }
            return
        }
        viewModelScope.launch {
            handle(tracking.update(stopped.id, stopped.toDraft().copy(endAt = newEnd))) { saved ->
                _state.update { it.copy(justStopped = saved as? SleepEvent) }
            }
        }
    }

    fun saveStoppedDetails(methodOrPlace: String, notes: String) {
        val stopped = _state.value.justStopped ?: return
        viewModelScope.launch {
            handle(tracking.update(stopped.id, stopped.toDraft().copy(methodOrPlace = methodOrPlace, notes = notes))) {
                _state.update { it.copy(justStopped = null) }
            }
        }
    }

    /** "Desfazer" depois de "Acordou": reabre o timer com o mesmo início. */
    fun undoStop() {
        val stopped = _state.value.justStopped ?: return
        viewModelScope.launch {
            handle(tracking.update(stopped.id, stopped.toDraft().copy(endAt = null))) { _state.update { it.copy(justStopped = null) } }
        }
    }

    fun dismissStopped() = _state.update { it.copy(justStopped = null) }

    /** "Cancelar registro": descarta o sono em andamento (a UI pede confirmação antes). */
    fun cancelOpenSleep() {
        val open = _state.value.openSleep ?: return
        viewModelScope.launch { tracking.delete(open.id) }
    }

    // ---- ações rápidas ---------------------------------------------------------------------------------------------
    /** Fralda em 1 toque no tipo (RF-018-A3). */
    fun quickDiaper(type: DiaperType) {
        viewModelScope.launch {
            val r = tracking.create(babyId, DiaperDraft(clock.instant(), type))
            handle(r) { saved -> _state.update { it.copy(message = TodayMessage.DiaperSaved(saved.id)) } }
        }
    }

    fun undoCreated(eventId: String) {
        viewModelScope.launch { tracking.delete(eventId) }
        dismissMessage()
    }

    fun dismissMessage() = _state.update { it.copy(message = null) }

    fun syncNow() {
        viewModelScope.launch {
            val msg = when (syncEngine.syncNow()) {
                SyncRunResult.Done -> null
                SyncRunResult.Offline -> TodayMessage.SyncOffline
                SyncRunResult.NotAvailable -> TodayMessage.SyncNotAvailable
                is SyncRunResult.Failed -> TodayMessage.Failed(null)
            }
            if (msg != null) _state.update { it.copy(message = msg) }
        }
    }

    private inline fun handle(result: SaveResult<TrackedEvent>, onSaved: (TrackedEvent) -> Unit) {
        when (result) {
            is SaveResult.Saved -> onSaved(result.value)
            is SaveResult.Rejected -> _state.update {
                it.copy(
                    message = when (result.reason) {
                        Rejection.ReadOnly -> TodayMessage.ReadOnly
                        is Rejection.OpenSleepExists -> TodayMessage.OpenSleepExists
                        else -> TodayMessage.Failed(null)
                    },
                )
            }
            is SaveResult.Invalid -> _state.update { it.copy(message = TodayMessage.Failed(null)) }
            is SaveResult.Failed -> _state.update { it.copy(message = TodayMessage.Failed(result.error)) }
        }
    }
}

internal fun SleepEvent.toDraft(): SleepDraft =
    SleepDraft(sleepType, startAt, endAt, methodOrPlace, notes, source)
