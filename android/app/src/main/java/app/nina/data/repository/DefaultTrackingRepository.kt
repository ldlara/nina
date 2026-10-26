package app.nina.data.repository

import androidx.room.withTransaction
import app.nina.data.local.BabyDao
import app.nina.data.local.EventEntity
import app.nina.data.local.MutationDao
import app.nina.data.local.MutationEntity
import app.nina.data.local.NinaDatabase
import app.nina.data.local.TrackingDao
import app.nina.data.sync.MutationWire
import app.nina.domain.model.AppError
import app.nina.domain.model.FeedingType
import app.nina.domain.model.MutationOp
import app.nina.domain.model.Outcome
import app.nina.domain.model.Role
import app.nina.domain.model.SyncEntityType
import app.nina.domain.tracking.BottleDraft
import app.nina.domain.tracking.BreastfeedingDraft
import app.nina.domain.tracking.DiaperDraft
import app.nina.domain.tracking.EventDraft
import app.nina.domain.tracking.EventValidator
import app.nina.domain.tracking.FeedingEvent
import app.nina.domain.tracking.MutationStatus
import app.nina.domain.tracking.PumpingDraft
import app.nina.domain.tracking.QuickDefaults
import app.nina.domain.tracking.Rejection
import app.nina.domain.tracking.SaveResult
import app.nina.domain.tracking.SleepDraft
import app.nina.domain.tracking.SleepEvent
import app.nina.domain.tracking.SyncState
import app.nina.domain.tracking.TrackedEvent
import app.nina.domain.tracking.TrackingRepository
import app.nina.domain.tracking.WakeDraft
import app.nina.domain.tracking.WakeEvent
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import java.time.Clock
import java.time.Instant
import java.util.UUID

/**
 * Offline-first: cada escrita grava o registro local **e** a mutação na mesma transação Room (ou as duas, ou nenhuma).
 * Nada aqui toca a rede. Regras de fila:
 *  - criar: linha local `PENDING` + mutação `CREATE` (`base_version = 0`);
 *  - editar registro ainda não aceito pelo servidor: reescreve o `data` da própria `CREATE` (sem mutação extra);
 *  - editar registro já confirmado: mutação `UPDATE` só com os campos alterados e `base_version` = versão conhecida;
 *  - excluir: tombstone local + `DELETE`; se o servidor nunca viu o registro, descarta-o junto com a fila dele.
 */
class DefaultTrackingRepository(
    private val database: NinaDatabase,
    private val dao: TrackingDao,
    private val queue: MutationDao,
    private val babies: BabyDao,
    private val clock: Clock,
    private val deviceId: () -> String,
    private val validator: EventValidator,
    private val newId: () -> String = { UUID.randomUUID().toString() },
    /** Chamado depois de cada escrita para o motor de sync (stub hoje, rede na Onda 5) acordar. */
    private val onLocalWrite: () -> Unit = {},
) : TrackingRepository {

    // ---- leitura -------------------------------------------------------------------------------------------------
    override fun observeTimeline(babyId: String, limit: Int): Flow<List<TrackedEvent>> =
        dao.observeTimeline(babyId, limit).map { l -> l.mapNotNull { it.toTracked() } }

    override fun observeRange(babyId: String, from: Instant, to: Instant): Flow<List<TrackedEvent>> =
        dao.observeRange(babyId, from.toEpochMilli(), to.toEpochMilli()).map { l -> l.mapNotNull { it.toTracked() } }

    override fun observeEvent(id: String): Flow<TrackedEvent?> = dao.observeEvent(id).map { it?.toTracked() }

    override fun observeOpenSleep(babyId: String): Flow<SleepEvent?> =
        dao.observeOpenSleep(babyId).map { it?.toTracked() as? SleepEvent }

    override fun observeLastFeeding(babyId: String): Flow<FeedingEvent?> =
        dao.observeLastFeeding(babyId).map { it?.toTracked() as? FeedingEvent }

    override fun observeLastClosedSleep(babyId: String): Flow<SleepEvent?> =
        dao.observeLastClosedSleep(babyId).map { it?.toTracked() as? SleepEvent }

    override fun observeWakeEvents(sleepSessionId: String): Flow<List<WakeEvent>> =
        dao.observeWakeEvents(sleepSessionId).map { l -> l.mapNotNull { it.toWake() } }

    override suspend fun quickDefaults(babyId: String): QuickDefaults {
        val breast = dao.lastOfType(babyId, SyncEntityType.FEEDING_SESSION.name, FeedingType.BREASTFEEDING.name)?.toTracked() as? FeedingEvent
        val bottle = dao.lastOfType(babyId, SyncEntityType.FEEDING_SESSION.name, FeedingType.BOTTLE.name)?.toTracked() as? FeedingEvent
        val pump = dao.lastOfType(babyId, SyncEntityType.PUMPING_SESSION.name, null)?.toTracked() as? app.nina.domain.tracking.PumpingEvent
        return QuickDefaults(
            lastBreastSide = breast?.side,
            lastBreastMinutes = breast?.duration?.toMinutes()?.toInt(),
            lastBottleVolumeMl = bottle?.volumeMl,
            lastMilkType = bottle?.milkType,
            lastPumpingSide = pump?.side,
            lastPumpingVolumeMl = pump?.volumeMl,
            lastPumpingMinutes = pump?.duration?.toMinutes()?.toInt(),
        )
    }

    override suspend fun hasSleepOverlap(babyId: String, start: Instant, end: Instant?, excludeId: String?): Boolean =
        dao.countSleepOverlap(babyId, start.toEpochMilli(), end?.toEpochMilli() ?: Long.MAX_VALUE, excludeId ?: "") > 0

    // ---- escrita -------------------------------------------------------------------------------------------------
    override suspend fun create(babyId: String, draft: EventDraft): SaveResult<TrackedEvent> = guarded {
        val issues = validator.validate(draft)
        if (issues.isNotEmpty()) return@guarded SaveResult.Invalid(issues)
        val baby = babies.get(babyId) ?: return@guarded SaveResult.Rejected(Rejection.BabyNotFound)
        if (!canWrite(baby.myRole)) return@guarded SaveResult.Rejected(Rejection.ReadOnly)

        val now = clock.instant().toEpochMilli()
        val entity = draft.toNewEntity(newId(), babyId, baby.timezone, now)
        database.withTransaction<SaveResult<TrackedEvent>> {
            // Dentro da transação: dois toques rápidos em "Dormiu" não criam dois timers (INV-02).
            if (draft is SleepDraft && draft.endAt == null) {
                dao.getOpenSleep(babyId)?.let { return@withTransaction SaveResult.Rejected(Rejection.OpenSleepExists(it.id)) }
            }
            dao.upsert(entity)
            enqueue(entity, MutationOp.CREATE, baseVersion = 0, payload = MutationWire.createData(entity).toString(), now = now)
            SaveResult.Saved(entity.toTracked()!!)
        }.also { if (it is SaveResult.Saved) onLocalWrite() }
    }

    override suspend fun update(id: String, draft: EventDraft): SaveResult<TrackedEvent> = guarded {
        val existing = dao.get(id)?.takeIf { !it.deleted && it.entityType != SyncEntityType.WAKE_EVENT.name }
            ?: return@guarded SaveResult.Rejected(Rejection.EventNotFound)
        val baby = babies.get(existing.babyId) ?: return@guarded SaveResult.Rejected(Rejection.BabyNotFound)
        if (!canWrite(baby.myRole)) return@guarded SaveResult.Rejected(Rejection.ReadOnly)
        if (!draft.matches(existing)) return@guarded SaveResult.Rejected(Rejection.TypeMismatch)
        val issues = validator.validate(draft)
        if (issues.isNotEmpty()) return@guarded SaveResult.Invalid(issues)

        val now = clock.instant().toEpochMilli()
        database.withTransaction<SaveResult<TrackedEvent>> {
            if (draft is SleepDraft && draft.endAt == null) {
                dao.getOpenSleep(existing.babyId)?.takeIf { it.id != id }
                    ?.let { return@withTransaction SaveResult.Rejected(Rejection.OpenSleepExists(it.id)) }
            }
            val changed = draft.applyTo(existing).copy(updatedAt = now)
            val patch = MutationWire.patchData(existing, changed)
            if (patch.isEmpty()) return@withTransaction SaveResult.Saved(existing.toTracked()!!)
            val next = changed.copy(syncState = SyncState.PENDING.name)
            dao.upsert(next)
            val unsent = queue.unsentCreate(id)
            if (unsent != null) {
                queue.rewriteCreate(unsent.mutationId, MutationWire.createData(next).toString())
            } else {
                enqueue(next, MutationOp.UPDATE, baseVersion = existing.version, payload = patch.toString(), now = now)
            }
            SaveResult.Saved(next.toTracked()!!)
        }.also { if (it is SaveResult.Saved) onLocalWrite() }
    }

    override suspend fun delete(id: String): Outcome<Unit> = guardedOutcome {
        val existing = dao.get(id) ?: return@guardedOutcome Outcome.Success(Unit)
        if (existing.entityType == SyncEntityType.WAKE_EVENT.name) return@guardedOutcome deleteWake(id)
        val baby = babies.get(existing.babyId)
        if (baby != null && !canWrite(baby.myRole)) return@guardedOutcome Outcome.Failure(AppError.Api(403, "FORBIDDEN_ROLE"))
        val now = clock.instant().toEpochMilli()
        database.withTransaction {
            // Despertares dependem do sono: saem primeiro (o servidor valida o vínculo apenas na criação).
            if (existing.entityType == SyncEntityType.SLEEP_SESSION.name) {
                dao.wakeEventsOf(id).filter { !it.deleted }.forEach { removeEntity(it, now) }
            }
            removeEntity(existing, now)
        }
        onLocalWrite()
        Outcome.Success(Unit)
    }

    override suspend fun createWake(babyId: String, draft: WakeDraft): SaveResult<WakeEvent> = guarded {
        val session = dao.get(draft.sleepSessionId)?.takeIf { !it.deleted && it.babyId == babyId }?.toTracked() as? SleepEvent
        val issues = validator.validate(draft, session)
        if (issues.isNotEmpty()) return@guarded SaveResult.Invalid(issues)
        val baby = babies.get(babyId) ?: return@guarded SaveResult.Rejected(Rejection.BabyNotFound)
        if (!canWrite(baby.myRole)) return@guarded SaveResult.Rejected(Rejection.ReadOnly)
        val now = clock.instant().toEpochMilli()
        val entity = EventEntity(
            id = newId(), babyId = babyId, entityType = SyncEntityType.WAKE_EVENT.name, version = 0, tz = baby.timezone,
            startAt = draft.startedAt.toEpochMilli(), endAt = draft.endedAt.toEpochMilli(),
            sleepType = null, sleepSource = null, methodOrPlace = null, notes = null, feedingType = null, side = null,
            volumeMl = null, milkType = null, diaperType = null, sleepSessionId = draft.sleepSessionId,
            wakeSource = draft.source.name, lastModifiedByName = null, createdAt = now, updatedAt = now,
            deleted = false, syncState = SyncState.PENDING.name,
        )
        database.withTransaction {
            dao.upsert(entity)
            enqueue(entity, MutationOp.CREATE, 0, MutationWire.createData(entity).toString(), now)
        }
        onLocalWrite()
        SaveResult.Saved(entity.toWake()!!)
    }

    override suspend fun updateWake(id: String, draft: WakeDraft): SaveResult<WakeEvent> = guarded {
        val existing = dao.get(id)?.takeIf { !it.deleted && it.entityType == SyncEntityType.WAKE_EVENT.name }
            ?: return@guarded SaveResult.Rejected(Rejection.EventNotFound)
        val session = dao.get(existing.sleepSessionId.orEmpty())?.takeIf { !it.deleted }?.toTracked() as? SleepEvent
        val issues = validator.validate(draft.copy(sleepSessionId = existing.sleepSessionId.orEmpty()), session)
        if (issues.isNotEmpty()) return@guarded SaveResult.Invalid(issues)
        val baby = babies.get(existing.babyId) ?: return@guarded SaveResult.Rejected(Rejection.BabyNotFound)
        if (!canWrite(baby.myRole)) return@guarded SaveResult.Rejected(Rejection.ReadOnly)
        val now = clock.instant().toEpochMilli()
        // Correção manual marca MANUAL (contrato, `EventPatch.source`).
        val changed = existing.copy(
            startAt = draft.startedAt.toEpochMilli(), endAt = draft.endedAt.toEpochMilli(),
            wakeSource = "MANUAL", updatedAt = now, syncState = SyncState.PENDING.name,
        )
        database.withTransaction {
            val patch = MutationWire.patchData(existing, changed)
            if (patch.isEmpty()) return@withTransaction
            dao.upsert(changed)
            val unsent = queue.unsentCreate(id)
            if (unsent != null) queue.rewriteCreate(unsent.mutationId, MutationWire.createData(changed).toString())
            else enqueue(changed, MutationOp.UPDATE, existing.version, patch.toString(), now)
        }
        onLocalWrite()
        SaveResult.Saved(changed.toWake()!!)
    }

    override suspend fun deleteWake(id: String): Outcome<Unit> = guardedOutcome {
        val existing = dao.get(id) ?: return@guardedOutcome Outcome.Success(Unit)
        val baby = babies.get(existing.babyId)
        if (baby != null && !canWrite(baby.myRole)) return@guardedOutcome Outcome.Failure(AppError.Api(403, "FORBIDDEN_ROLE"))
        val now = clock.instant().toEpochMilli()
        database.withTransaction { removeEntity(existing, now) }
        onLocalWrite()
        Outcome.Success(Unit)
    }

    // ---- internos --------------------------------------------------------------------------------------------------
    private fun canWrite(roleName: String): Boolean {
        val role = enumOrUnrecognized(roleName, Role.UNRECOGNIZED)
        return role == Role.OWNER || role == Role.CAREGIVER
    }

    private suspend fun enqueue(e: EventEntity, op: MutationOp, baseVersion: Int, payload: String?, now: Long) {
        queue.insert(
            MutationEntity(
                mutationId = newId(),
                babyId = e.babyId,
                entityType = e.entityType,
                entityId = e.id,
                op = op.name,
                baseVersion = baseVersion,
                clientCreatedAt = now,
                deviceId = deviceId(),
                payload = payload,
                status = MutationStatus.PENDING.name,
                attempts = 0,
                nextAttemptAt = 0,
                lastErrorCode = null,
            ),
        )
    }

    /** Dentro de transação: descarta (nunca enviado) ou faz tombstone + `DELETE`. */
    private suspend fun removeEntity(e: EventEntity, now: Long) {
        val unsentCreate = queue.unsentCreate(e.id)
        val createInFlight = queue.forEntity(e.id).any { it.op == MutationOp.CREATE.name && it.status == MutationStatus.IN_FLIGHT.name }
        if (unsentCreate != null || (e.version == 0 && !createInFlight)) {
            queue.deleteForEntity(e.id)
            dao.hardDelete(e.id)
        } else {
            dao.markDeleted(e.id, now)
            // Se a CREATE está em voo o servidor vai criar com versão 1; o `base_version` real é ajustado em `markApplied`.
            enqueue(e, MutationOp.DELETE, e.version, null, now)
        }
    }

    private inline fun <T> guarded(block: () -> SaveResult<T>): SaveResult<T> = try {
        block()
    } catch (ce: kotlinx.coroutines.CancellationException) {
        throw ce
    } catch (t: Throwable) {
        SaveResult.Failed(AppError.Unexpected(t.message))
    }

    private inline fun guardedOutcome(block: () -> Outcome<Unit>): Outcome<Unit> = try {
        block()
    } catch (ce: kotlinx.coroutines.CancellationException) {
        throw ce
    } catch (t: Throwable) {
        Outcome.Failure(AppError.Unexpected(t.message))
    }
}

// ---- rascunho <-> entidade ---------------------------------------------------------------------------------------------

private fun blank(id: String, babyId: String, type: SyncEntityType, tz: String, start: Long, now: Long) = EventEntity(
    id = id, babyId = babyId, entityType = type.name, version = 0, tz = tz, startAt = start, endAt = null,
    sleepType = null, sleepSource = null, methodOrPlace = null, notes = null, feedingType = null, side = null,
    volumeMl = null, milkType = null, diaperType = null, sleepSessionId = null, wakeSource = null,
    lastModifiedByName = null, createdAt = now, updatedAt = now, deleted = false, syncState = SyncState.PENDING.name,
)

private fun String?.blankToNull(): String? = this?.trim()?.takeIf { it.isNotEmpty() }

internal fun EventDraft.toNewEntity(id: String, babyId: String, tz: String, now: Long): EventEntity = when (this) {
    is SleepDraft -> blank(id, babyId, SyncEntityType.SLEEP_SESSION, tz, startAt.toEpochMilli(), now).copy(
        endAt = endAt?.toEpochMilli(), sleepType = sleepType.name, sleepSource = source.name,
        methodOrPlace = methodOrPlace.blankToNull(), notes = notes.blankToNull(),
    )
    is BreastfeedingDraft -> blank(id, babyId, SyncEntityType.FEEDING_SESSION, tz, startAt.toEpochMilli(), now).copy(
        endAt = endAt?.toEpochMilli(), feedingType = FeedingType.BREASTFEEDING.name, side = side?.name, notes = notes.blankToNull(),
    )
    is BottleDraft -> blank(id, babyId, SyncEntityType.FEEDING_SESSION, tz, startAt.toEpochMilli(), now).copy(
        endAt = endAt?.toEpochMilli(), feedingType = FeedingType.BOTTLE.name, volumeMl = volumeMl,
        milkType = milkType?.name, notes = notes.blankToNull(),
    )
    is PumpingDraft -> blank(id, babyId, SyncEntityType.PUMPING_SESSION, tz, startAt.toEpochMilli(), now).copy(
        endAt = endAt?.toEpochMilli(), volumeMl = volumeMl, side = side?.name,
    )
    is DiaperDraft -> blank(id, babyId, SyncEntityType.DIAPER_EVENT, tz, occurredAt.toEpochMilli(), now).copy(
        diaperType = diaperType?.name, notes = notes.blankToNull(),
    )
}

/** Aplica o rascunho sobre o registro existente mantendo o que é imutável (`feeding_type`, `source`, `tz`). */
internal fun EventDraft.applyTo(e: EventEntity): EventEntity = when (this) {
    is SleepDraft -> e.copy(
        sleepType = sleepType.name, startAt = startAt.toEpochMilli(), endAt = endAt?.toEpochMilli(),
        methodOrPlace = methodOrPlace.blankToNull(), notes = notes.blankToNull(),
    )
    is BreastfeedingDraft -> e.copy(startAt = startAt.toEpochMilli(), endAt = endAt?.toEpochMilli(), side = side?.name, notes = notes.blankToNull())
    is BottleDraft -> e.copy(
        startAt = startAt.toEpochMilli(), endAt = endAt?.toEpochMilli(), volumeMl = volumeMl,
        milkType = milkType?.name, notes = notes.blankToNull(),
    )
    is PumpingDraft -> e.copy(startAt = startAt.toEpochMilli(), endAt = endAt?.toEpochMilli(), volumeMl = volumeMl, side = side?.name)
    is DiaperDraft -> e.copy(startAt = occurredAt.toEpochMilli(), diaperType = diaperType?.name, notes = notes.blankToNull())
}

internal fun EventDraft.matches(e: EventEntity): Boolean = when (this) {
    is SleepDraft -> e.entityType == SyncEntityType.SLEEP_SESSION.name
    is BreastfeedingDraft -> e.entityType == SyncEntityType.FEEDING_SESSION.name && e.feedingType == FeedingType.BREASTFEEDING.name
    is BottleDraft -> e.entityType == SyncEntityType.FEEDING_SESSION.name && e.feedingType == FeedingType.BOTTLE.name
    is PumpingDraft -> e.entityType == SyncEntityType.PUMPING_SESSION.name
    is DiaperDraft -> e.entityType == SyncEntityType.DIAPER_EVENT.name
}
