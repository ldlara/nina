package app.nina.ui.timeline

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.nina.domain.model.AppError
import app.nina.domain.model.Baby
import app.nina.domain.model.Role
import app.nina.domain.repository.BabyRepository
import app.nina.domain.model.DiaperType
import app.nina.domain.tracking.DaySummary
import app.nina.domain.tracking.DiaperDraft
import app.nina.domain.tracking.SaveResult
import app.nina.domain.tracking.DayWindow
import app.nina.domain.tracking.DiaperEvent
import app.nina.domain.tracking.FeedingEvent
import app.nina.domain.tracking.PumpingEvent
import app.nina.domain.tracking.SleepEvent
import app.nina.domain.tracking.SyncEngine
import app.nina.domain.tracking.SyncIndicator
import app.nina.domain.tracking.SyncRunResult
import app.nina.domain.tracking.TrackedEvent
import app.nina.domain.tracking.TrackingRepository
import app.nina.domain.tracking.localDate
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.catch
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.time.Clock
import java.time.LocalDate
import java.time.ZoneId

/** Chips de filtro (ux-spec 4.4). */
enum class TimelineFilter {
    ALL, SLEEP, FEEDING, DIAPER, PUMPING;

    fun accepts(e: TrackedEvent): Boolean = when (this) {
        ALL -> true
        SLEEP -> e is SleepEvent
        FEEDING -> e is FeedingEvent
        DIAPER -> e is DiaperEvent
        PUMPING -> e is PumpingEvent
    }
}

data class DaySection(
    val date: LocalDate,
    val isToday: Boolean,
    /** Totais do dia (sempre sobre todos os tipos, independentemente do filtro). `null` quando o dia pode estar incompleto. */
    val summary: DaySummary?,
    val items: List<TrackedEvent>,
)

data class TimelineUiState(
    val loading: Boolean = true,
    val baby: Baby? = null,
    val zone: ZoneId = ZoneId.of("UTC"),
    val filter: TimelineFilter = TimelineFilter.ALL,
    /** Sessão de sono em andamento, fixa no topo ("em andamento", RF-019-A7). */
    val openSleep: SleepEvent? = null,
    val sections: List<DaySection> = emptyList(),
    /** Há mais eventos locais além da janela carregada (rolagem infinita). */
    val hasMore: Boolean = false,
    /** Nenhum evento no banco local para este bebê. */
    val noEventsAtAll: Boolean = false,
    val sync: SyncIndicator = SyncIndicator.Synced,
    val canRecord: Boolean = false,
    val error: AppError? = null,
    val syncMessage: SyncRunResult? = null,
    /** Fralda salva pela ação rápida; a UI oferece "Desfazer" por 8 s. */
    val diaperSavedId: String? = null,
    val saveError: Boolean = false,
)

class TimelineViewModel(
    private val babyId: String,
    private val babies: BabyRepository,
    private val tracking: TrackingRepository,
    private val syncIndicator: Flow<SyncIndicator>,
    private val syncEngine: SyncEngine,
    private val clock: Clock,
    private val pageSize: Int = PAGE_SIZE,
) : ViewModel() {

    private val _state = MutableStateFlow(TimelineUiState())
    val state: StateFlow<TimelineUiState> = _state.asStateFlow()

    private val limit = MutableStateFlow(pageSize)
    private val filter = MutableStateFlow(TimelineFilter.ALL)
    private var loadJob: Job? = null

    init {
        viewModelScope.launch {
            babies.observeBaby(babyId).collect { baby ->
                _state.update {
                    it.copy(
                        baby = baby,
                        zone = baby?.let { b -> runCatching { ZoneId.of(b.timezone) }.getOrNull() } ?: it.zone,
                        canRecord = baby != null && (baby.myRole == Role.OWNER || baby.myRole == Role.CAREGIVER),
                    )
                }
            }
        }
        viewModelScope.launch { syncIndicator.catch { }.collect { s -> _state.update { it.copy(sync = s) } } }
        load()
    }

    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    private fun load() {
        loadJob?.cancel()
        _state.update { it.copy(error = null) }
        loadJob = viewModelScope.launch {
            combine(limit, filter, babies.observeBaby(babyId)) { l, f, b -> Triple(l, f, b) }
                .flatMapLatest { (l, f, b) ->
                    tracking.observeTimeline(babyId, l).map { events ->
                        val zone = b?.let { runCatching { ZoneId.of(it.timezone) }.getOrNull() } ?: ZoneId.of("UTC")
                        buildModel(events, f, zone, l)
                    }
                }
                .catch { e -> _state.update { it.copy(loading = false, error = AppError.Unexpected(e.message)) } }
                .collect { model -> _state.update { it.copy(loading = false, error = null).withModel(model) } }
        }
    }

    private data class Model(
        val open: SleepEvent?,
        val sections: List<DaySection>,
        val hasMore: Boolean,
        val none: Boolean,
        val filter: TimelineFilter,
    )

    private fun TimelineUiState.withModel(m: Model) =
        copy(openSleep = m.open, sections = m.sections, hasMore = m.hasMore, noEventsAtAll = m.none, filter = m.filter)

    private fun buildModel(events: List<TrackedEvent>, f: TimelineFilter, zone: ZoneId, requested: Int): Model {
        val hasMore = events.size >= requested
        val open = events.firstOrNull { it is SleepEvent && it.isOpen } as? SleepEvent
        val closed = events.filterNot { it === open }
        val today = clock.instant().localDate(zone)
        val byDay = closed.groupBy { it.startInstant.localDate(zone) }
        val oldest = byDay.keys.minOrNull()
        val dates = (byDay.keys + today).toSortedSet(compareByDescending { it })
        val sections = dates.map { date ->
            val all = byDay[date].orEmpty()
            // O dia mais antigo da janela pode estar cortado: sem totais enquanto houver mais a carregar.
            val partial = hasMore && date == oldest
            DaySection(
                date = date,
                isToday = date == today,
                summary = if (partial) null else DaySummary.of(all, DayWindow.of(date, zone)),
                items = all.filter { f.accepts(it) },
            )
        }.filter { it.isToday || it.items.isNotEmpty() }
        return Model(open, sections, hasMore, events.isEmpty() && !hasMore, f)
    }

    fun setFilter(f: TimelineFilter) {
        filter.value = f
    }

    /** Rolagem infinita: aumenta a janela local (leitura imediata do Room, RNF-005). */
    fun loadMore() {
        if (_state.value.hasMore) limit.update { it + pageSize }
    }

    fun retry() = load()

    fun syncNow() {
        viewModelScope.launch {
            val r = syncEngine.syncNow()
            _state.update { it.copy(syncMessage = r) }
        }
    }

    fun dismissSyncMessage() = _state.update { it.copy(syncMessage = null) }

    /** Fralda em 1 toque no tipo (RF-018-A3), agora, no fuso do bebê. */
    fun quickDiaper(type: DiaperType) {
        viewModelScope.launch {
            when (val r = tracking.create(babyId, DiaperDraft(clock.instant(), type))) {
                is SaveResult.Saved -> _state.update { it.copy(diaperSavedId = r.value.id) }
                else -> _state.update { it.copy(saveError = true) }
            }
        }
    }

    fun undoDiaper() {
        val id = _state.value.diaperSavedId ?: return
        viewModelScope.launch { tracking.delete(id) }
        dismissDiaperMessage()
    }

    fun dismissDiaperMessage() = _state.update { it.copy(diaperSavedId = null, saveError = false) }

    companion object {
        const val PAGE_SIZE = 100
    }
}
