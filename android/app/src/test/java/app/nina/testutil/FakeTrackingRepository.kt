package app.nina.testutil

import app.nina.domain.model.DiaperType
import app.nina.domain.model.Outcome
import app.nina.domain.tracking.BottleDraft
import app.nina.domain.tracking.BreastfeedingDraft
import app.nina.domain.tracking.DiaperDraft
import app.nina.domain.tracking.DiaperEvent
import app.nina.domain.tracking.EventDraft
import app.nina.domain.tracking.FeedingEvent
import app.nina.domain.tracking.PumpingDraft
import app.nina.domain.tracking.PumpingEvent
import app.nina.domain.tracking.QuickDefaults
import app.nina.domain.tracking.SaveResult
import app.nina.domain.tracking.SleepDraft
import app.nina.domain.tracking.SleepEvent
import app.nina.domain.tracking.SyncState
import app.nina.domain.tracking.TrackedEvent
import app.nina.domain.tracking.TrackingRepository
import app.nina.domain.tracking.WakeDraft
import app.nina.domain.tracking.WakeEvent
import app.nina.domain.model.FeedingType
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.map
import java.time.Instant

/** Repositório em memória para testes de ViewModel (a persistência real é testada com Room). */
class FakeTrackingRepository(private val babyTz: String = "America/Sao_Paulo") : TrackingRepository {
    val events = MutableStateFlow<List<TrackedEvent>>(emptyList())
    val wakes = MutableStateFlow<List<WakeEvent>>(emptyList())
    var defaults = QuickDefaults()
    var failNextCreate: SaveResult<TrackedEvent>? = null
    val created = mutableListOf<EventDraft>()
    val deleted = mutableListOf<String>()
    private var n = 0

    private fun alive() = events.value.sortedWith(compareByDescending<TrackedEvent> { it.startInstant }.thenByDescending { it.id })

    override fun observeTimeline(babyId: String, limit: Int): Flow<List<TrackedEvent>> = events.map { alive().take(limit) }
    override fun observeRange(babyId: String, from: Instant, to: Instant): Flow<List<TrackedEvent>> =
        events.map { alive().filter { it.startInstant < to && it.startInstant >= from } }
    override fun observeEvent(id: String): Flow<TrackedEvent?> = events.map { l -> l.firstOrNull { it.id == id } }
    override fun observeOpenSleep(babyId: String): Flow<SleepEvent?> = events.map { l -> l.filterIsInstance<SleepEvent>().firstOrNull { it.isOpen } }
    override fun observeLastFeeding(babyId: String): Flow<FeedingEvent?> = events.map { alive().filterIsInstance<FeedingEvent>().firstOrNull() }
    override fun observeLastClosedSleep(babyId: String): Flow<SleepEvent?> =
        events.map { alive().filterIsInstance<SleepEvent>().filter { !it.isOpen }.maxByOrNull { it.endAt!! } }
    override fun observeWakeEvents(sleepSessionId: String): Flow<List<WakeEvent>> = wakes.map { l -> l.filter { it.sleepSessionId == sleepSessionId } }
    override suspend fun quickDefaults(babyId: String) = defaults
    override suspend fun hasSleepOverlap(babyId: String, start: Instant, end: Instant?, excludeId: String?) = false

    private fun build(id: String, draft: EventDraft): TrackedEvent {
        val now = Instant.EPOCH
        return when (draft) {
            is SleepDraft -> SleepEvent(id, "b-1", 0, babyTz, SyncState.PENDING, now, null, draft.sleepType, draft.startAt, draft.endAt, draft.methodOrPlace, draft.notes, draft.source)
            is BreastfeedingDraft -> FeedingEvent(id, "b-1", 0, babyTz, SyncState.PENDING, now, null, FeedingType.BREASTFEEDING, draft.startAt, draft.endAt, draft.side, null, null, draft.notes)
            is BottleDraft -> FeedingEvent(id, "b-1", 0, babyTz, SyncState.PENDING, now, null, FeedingType.BOTTLE, draft.startAt, draft.endAt, null, draft.volumeMl, draft.milkType, draft.notes)
            is PumpingDraft -> PumpingEvent(id, "b-1", 0, babyTz, SyncState.PENDING, now, null, draft.startAt, draft.endAt!!, draft.volumeMl, draft.side)
            is DiaperDraft -> DiaperEvent(id, "b-1", 0, babyTz, SyncState.PENDING, now, null, draft.occurredAt, draft.diaperType ?: DiaperType.UNSPECIFIED, draft.notes)
        }
    }

    override suspend fun create(babyId: String, draft: EventDraft): SaveResult<TrackedEvent> {
        failNextCreate?.let { failNextCreate = null; return it }
        created += draft
        val e = build("ev-${++n}", draft)
        events.value = events.value + e
        return SaveResult.Saved(e)
    }

    override suspend fun update(id: String, draft: EventDraft): SaveResult<TrackedEvent> {
        val e = build(id, draft)
        events.value = events.value.filterNot { it.id == id } + e
        return SaveResult.Saved(e)
    }

    override suspend fun delete(id: String): Outcome<Unit> {
        deleted += id
        events.value = events.value.filterNot { it.id == id }
        return Outcome.Success(Unit)
    }

    override suspend fun createWake(babyId: String, draft: WakeDraft): SaveResult<WakeEvent> {
        val w = WakeEvent("w-${++n}", babyId, 0, SyncState.PENDING, draft.sleepSessionId, draft.startedAt, draft.endedAt, draft.source)
        wakes.value = wakes.value + w
        return SaveResult.Saved(w)
    }
    override suspend fun updateWake(id: String, draft: WakeDraft): SaveResult<WakeEvent> = createWake("b-1", draft)
    override suspend fun deleteWake(id: String): Outcome<Unit> { wakes.value = wakes.value.filterNot { it.id == id }; return Outcome.Success(Unit) }
}
