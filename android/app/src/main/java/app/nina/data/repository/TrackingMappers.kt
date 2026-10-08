package app.nina.data.repository

import app.nina.data.local.EventEntity
import app.nina.data.local.MutationEntity
import app.nina.domain.model.BreastSide
import app.nina.domain.model.DiaperType
import app.nina.domain.model.FeedingType
import app.nina.domain.model.MilkType
import app.nina.domain.model.MutationOp
import app.nina.domain.model.SleepSource
import app.nina.domain.model.SleepType
import app.nina.domain.model.SyncEntityType
import app.nina.domain.model.WakeSource
import app.nina.domain.tracking.DiaperEvent
import app.nina.domain.tracking.FeedingEvent
import app.nina.domain.tracking.MutationStatus
import app.nina.domain.tracking.PumpingEvent
import app.nina.domain.tracking.QueuedMutation
import app.nina.domain.tracking.SleepEvent
import app.nina.domain.tracking.SyncState
import app.nina.domain.tracking.TrackedEvent
import app.nina.domain.tracking.WakeEvent
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import java.time.Instant

/** Nome do enum -> valor; qualquer coisa desconhecida vira [fallback] (tolerância a enums extensíveis, ADR-0009). */
inline fun <reified E : Enum<E>> enumOrUnrecognized(raw: String?, fallback: E): E =
    raw?.let { r -> enumValues<E>().firstOrNull { it.name == r } } ?: fallback

private val storeJson = Json { ignoreUnknownKeys = true }

fun String.toSyncState(): SyncState = SyncState.entries.firstOrNull { it.name == this } ?: SyncState.PENDING

fun EventEntity.toTracked(): TrackedEvent? {
    val sync = syncState.toSyncState()
    return when (entityType) {
        SyncEntityType.SLEEP_SESSION.name -> SleepEvent(
            id = id, babyId = babyId, version = version, tz = tz, syncState = sync,
            updatedAt = Instant.ofEpochMilli(updatedAt), lastModifiedByName = lastModifiedByName,
            sleepType = enumOrUnrecognized(sleepType, SleepType.UNRECOGNIZED),
            startAt = Instant.ofEpochMilli(startAt), endAt = endAt?.let(Instant::ofEpochMilli),
            methodOrPlace = methodOrPlace, notes = notes,
            source = enumOrUnrecognized(sleepSource, SleepSource.UNRECOGNIZED),
        )
        SyncEntityType.FEEDING_SESSION.name -> {
            val type = enumOrUnrecognized(feedingType, FeedingType.UNRECOGNIZED)
            FeedingEvent(
                id = id, babyId = babyId, version = version, tz = tz, syncState = sync,
                updatedAt = Instant.ofEpochMilli(updatedAt), lastModifiedByName = lastModifiedByName,
                feedingType = type,
                startAt = Instant.ofEpochMilli(startAt), endAt = endAt?.let(Instant::ofEpochMilli),
                // `side`, `volume_ml` e `milk_type` só se aplicam a certos tipos; nos demais são `null` (ADR-0009).
                side = if (type == FeedingType.BREASTFEEDING) side?.let { enumOrUnrecognized(it, BreastSide.UNRECOGNIZED) } else null,
                volumeMl = if (type == FeedingType.BOTTLE) volumeMl else null,
                milkType = if (type == FeedingType.BOTTLE) milkType?.let { enumOrUnrecognized(it, MilkType.UNRECOGNIZED) } else null,
                notes = notes,
            )
        }
        SyncEntityType.PUMPING_SESSION.name -> PumpingEvent(
            id = id, babyId = babyId, version = version, tz = tz, syncState = sync,
            updatedAt = Instant.ofEpochMilli(updatedAt), lastModifiedByName = lastModifiedByName,
            startAt = Instant.ofEpochMilli(startAt), endAt = Instant.ofEpochMilli(endAt ?: startAt),
            volumeMl = volumeMl, side = side?.let { enumOrUnrecognized(it, BreastSide.UNRECOGNIZED) },
        )
        SyncEntityType.DIAPER_EVENT.name -> DiaperEvent(
            id = id, babyId = babyId, version = version, tz = tz, syncState = sync,
            updatedAt = Instant.ofEpochMilli(updatedAt), lastModifiedByName = lastModifiedByName,
            occurredAt = Instant.ofEpochMilli(startAt),
            diaperType = enumOrUnrecognized(diaperType, DiaperType.UNRECOGNIZED), notes = notes,
        )
        else -> null
    }
}

fun EventEntity.toWake(): WakeEvent? =
    if (entityType != SyncEntityType.WAKE_EVENT.name || sleepSessionId == null) null
    else WakeEvent(
        id = id, babyId = babyId, version = version, syncState = syncState.toSyncState(),
        sleepSessionId = sleepSessionId, startedAt = Instant.ofEpochMilli(startAt),
        endedAt = Instant.ofEpochMilli(endAt ?: startAt),
        source = enumOrUnrecognized(wakeSource, WakeSource.UNRECOGNIZED),
    )

fun MutationEntity.toDomain(): QueuedMutation = QueuedMutation(
    seq = seq,
    mutationId = mutationId,
    babyId = babyId,
    entityType = enumOrUnrecognized(entityType, SyncEntityType.UNRECOGNIZED),
    entityId = entityId,
    op = enumOrUnrecognized(op, MutationOp.UNRECOGNIZED),
    baseVersion = baseVersion,
    clientCreatedAt = Instant.ofEpochMilli(clientCreatedAt),
    deviceId = deviceId,
    data = payload?.let { storeJson.parseToJsonElement(it) as? JsonObject },
    status = enumOrUnrecognized(status, MutationStatus.PENDING),
    attempts = attempts,
    nextAttemptAt = Instant.ofEpochMilli(nextAttemptAt),
    lastErrorCode = lastErrorCode,
)
