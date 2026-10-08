package app.nina.domain.tracking

import app.nina.domain.model.Outcome
import kotlinx.coroutines.flow.Flow
import java.time.Instant

/**
 * Repositório de registros (offline-first): **toda escrita grava primeiro no banco local** e enfileira a mutação na
 * mesma transação; nada aqui espera a rede (ADR-0003, RF-046). A leitura vem sempre do banco local.
 */
interface TrackingRepository {
    /** Eventos não excluídos, do mais recente ao mais antigo (`start_at` desc, desempate por `id`), sono em aberto no topo. */
    fun observeTimeline(babyId: String, limit: Int): Flow<List<TrackedEvent>>

    /** Eventos que intersectam [from, to) (início antes de `to` e fim/início em ou depois de `from`). */
    fun observeRange(babyId: String, from: Instant, to: Instant): Flow<List<TrackedEvent>>

    fun observeEvent(id: String): Flow<TrackedEvent?>
    fun observeOpenSleep(babyId: String): Flow<SleepEvent?>
    fun observeLastFeeding(babyId: String): Flow<FeedingEvent?>
    fun observeLastClosedSleep(babyId: String): Flow<SleepEvent?>
    fun observeWakeEvents(sleepSessionId: String): Flow<List<WakeEvent>>

    suspend fun quickDefaults(babyId: String): QuickDefaults

    /** Há outro sono (não excluído) que se sobrepõe ao intervalo? Sobreposição é aceita e sinalizada (ADR-0009). */
    suspend fun hasSleepOverlap(babyId: String, start: Instant, end: Instant?, excludeId: String?): Boolean

    suspend fun create(babyId: String, draft: EventDraft): SaveResult<TrackedEvent>
    suspend fun update(id: String, draft: EventDraft): SaveResult<TrackedEvent>

    /** Exclui: tombstone local + mutação `DELETE` (RB-008). Evento nunca enviado é descartado junto com a fila dele. */
    suspend fun delete(id: String): Outcome<Unit>

    suspend fun createWake(babyId: String, draft: WakeDraft): SaveResult<WakeEvent>
    suspend fun updateWake(id: String, draft: WakeDraft): SaveResult<WakeEvent>
    suspend fun deleteWake(id: String): Outcome<Unit>
}
