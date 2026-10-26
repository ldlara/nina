package app.nina.data.sync

import app.nina.data.local.EventEntity
import app.nina.domain.model.FeedingType
import app.nina.domain.model.SyncEntityType
import app.nina.domain.tracking.QueuedMutation
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.time.Instant

/**
 * Formato de envio do contrato `POST /sync/push` (openapi v1.0.1): `snake_case`, enums em MAIÚSCULAS, instantes ISO-8601 UTC.
 * Este objeto **não faz rede**: só monta o JSON que o motor de sync da Onda 5 enviará. `UNRECOGNIZED` (valor só do
 * cliente) nunca é incluído. Os esquemas de criação têm `additionalProperties: false`, então cada tipo envia só os
 * campos dele (ex.: mamada no peito não leva `volume_ml` nem `milk_type`).
 */
object MutationWire {

    /** Campos de `data` de `CREATE`: `SleepData`, `BreastFeedingData`, `BottleFeedingData`, `OtherFeedingData`, `PumpingData`, `DiaperData`, `WakeEventData`. */
    fun createData(e: EventEntity): JsonObject = JsonObject(fields(e, forCreate = true))

    /** `EventPatch`: somente campos que mudaram entre [old] e [new]; `null` explícito limpa campos anuláveis. */
    fun patchData(old: EventEntity, new: EventEntity): JsonObject {
        val before = fields(old, forCreate = false)
        val after = fields(new, forCreate = false)
        return JsonObject(after.filter { (k, v) -> before[k] != v })
    }

    private fun fields(e: EventEntity, forCreate: Boolean): Map<String, JsonElement> {
        val m = LinkedHashMap<String, JsonElement>()
        fun str(key: String, value: String?, nullable: Boolean = true) {
            if (value != null) m[key] = JsonPrimitive(value) else if (nullable) m[key] = JsonNull
        }
        fun enum(key: String, value: String?, nullable: Boolean = true) {
            when {
                value == "UNRECOGNIZED" -> Unit // nunca enviado
                value != null -> m[key] = JsonPrimitive(value)
                nullable -> m[key] = JsonNull
            }
        }
        fun time(key: String, ms: Long?, nullable: Boolean = true) {
            if (ms != null) m[key] = JsonPrimitive(Instant.ofEpochMilli(ms).toString()) else if (nullable) m[key] = JsonNull
        }
        fun int(key: String, v: Int?) {
            m[key] = if (v != null) JsonPrimitive(v) else JsonNull
        }

        when (e.entityType) {
            SyncEntityType.SLEEP_SESSION.name -> {
                enum("sleep_type", e.sleepType, nullable = false)
                time("start_at", e.startAt)
                time("end_at", e.endAt)
                str("tz", e.tz, nullable = false)
                str("method_or_place", e.methodOrPlace)
                str("notes", e.notes)
                if (forCreate) enum("source", e.sleepSource, nullable = false)
            }
            SyncEntityType.FEEDING_SESSION.name -> {
                if (forCreate) enum("feeding_type", e.feedingType, nullable = false)
                time("start_at", e.startAt)
                time("end_at", e.endAt)
                str("tz", e.tz, nullable = false)
                when (e.feedingType) {
                    FeedingType.BREASTFEEDING.name -> {
                        enum("side", e.side, nullable = false)
                        str("notes", e.notes)
                    }
                    FeedingType.BOTTLE.name -> {
                        int("volume_ml", e.volumeMl)
                        enum("milk_type", e.milkType)
                        str("notes", e.notes)
                    }
                    else -> str("notes", e.notes) // SOLID / OTHER: sem side, volume_ml nem milk_type
                }
            }
            SyncEntityType.PUMPING_SESSION.name -> {
                time("start_at", e.startAt)
                time("end_at", e.endAt)
                str("tz", e.tz, nullable = false)
                int("volume_ml", e.volumeMl)
                enum("side", e.side)
            }
            SyncEntityType.DIAPER_EVENT.name -> {
                time("occurred_at", e.startAt)
                enum("diaper_type", e.diaperType, nullable = false)
                str("tz", e.tz, nullable = false)
                str("notes", e.notes)
            }
            SyncEntityType.WAKE_EVENT.name -> {
                if (forCreate) str("sleep_session_id", e.sleepSessionId, nullable = false)
                time("started_at", e.startAt)
                time("ended_at", e.endAt)
                enum("source", e.wakeSource, nullable = false)
            }
        }
        return m
    }

    /** `SyncMutation` completo (CreateMutation, UpdateMutation ou DeleteMutation). */
    fun mutation(m: QueuedMutation): JsonObject {
        val map = LinkedHashMap<String, JsonElement>()
        map["mutation_id"] = JsonPrimitive(m.mutationId)
        map["op"] = JsonPrimitive(m.op.name)
        map["entity_type"] = JsonPrimitive(m.entityType.name)
        map["entity_id"] = JsonPrimitive(m.entityId)
        map["baby_id"] = JsonPrimitive(m.babyId)
        map["base_version"] = JsonPrimitive(m.baseVersion)
        map["client_created_at"] = JsonPrimitive(m.clientCreatedAt.toString())
        m.data?.let { map["data"] = it }
        return JsonObject(map)
    }

    /** `PushRequest` (até 100 mutações, em ordem). */
    fun pushRequest(deviceId: String, mutations: List<QueuedMutation>): JsonObject = JsonObject(
        mapOf(
            "device_id" to JsonPrimitive(deviceId),
            "mutations" to JsonArray(mutations.map(::mutation)),
        ),
    )
}
