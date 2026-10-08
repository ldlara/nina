package app.nina.data.repository

import app.nina.data.local.BabyDao
import app.nina.data.local.MembershipDao
import app.nina.data.remote.NinaApi
import app.nina.data.remote.apiCall
import app.nina.data.remote.dto.BabyCreateDto
import app.nina.domain.model.AppError
import app.nina.domain.model.Baby
import app.nina.domain.model.BabyDraft
import app.nina.domain.model.Outcome
import app.nina.domain.model.ReferenceData
import app.nina.domain.model.map
import app.nina.domain.repository.BabyRepository
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import java.util.UUID

class DefaultBabyRepository(
    private val api: NinaApi,
    private val babyDao: BabyDao,
    private val membershipDao: MembershipDao,
    private val newId: () -> String = { UUID.randomUUID().toString() },
) : BabyRepository {

    override fun observeBabies(): Flow<List<Baby>> = babyDao.observeAll().map { list -> list.map { it.toDomain() } }

    override fun observeBaby(id: String): Flow<Baby?> = babyDao.observe(id).map { it?.toDomain() }

    override suspend fun refreshBabies(): Outcome<Unit> =
        when (val r = apiCall { api.listBabies() }) {
            is Outcome.Success -> {
                babyDao.replaceAll(r.value.items.map { it.toDomain().toEntity() })
                Outcome.Success(Unit)
            }
            is Outcome.Failure -> r // falha de rede: mantém o cache
        }

    override suspend fun refreshBaby(id: String): Outcome<Baby> =
        when (val r = apiCall { api.getBaby(id) }) {
            is Outcome.Success -> {
                val baby = r.value.toDomain()
                babyDao.upsert(baby.toEntity())
                Outcome.Success(baby)
            }
            is Outcome.Failure -> {
                purgeIfAccessLost(r.error, id)
                r
            }
        }

    override suspend fun createBaby(draft: BabyDraft): Outcome<Baby> {
        val body = BabyCreateDto(
            id = newId(),
            displayName = draft.displayName.trim(),
            birthDate = draft.birthDate.toString(),
            dueDate = draft.dueDate?.toString(),
            sex = draft.sex,
            timezone = draft.timezone,
        )
        return apiCall { api.createBaby(newId(), body) }.map { dto ->
            dto.toDomain().also { babyDao.upsert(it.toEntity()) }
        }
    }

    override suspend fun updateBaby(current: Baby, draft: BabyDraft): Outcome<Baby> {
        val patch = buildMergePatch(current, draft)
        if (patch.isEmpty()) return Outcome.Success(current)
        val body = patch.toString().toRequestBody("application/merge-patch+json".toMediaType())
        return when (val r = apiCall { api.updateBaby(current.id, "\"${current.version}\"", body) }) {
            is Outcome.Success -> {
                val baby = r.value.toDomain()
                babyDao.upsert(baby.toEntity())
                Outcome.Success(baby)
            }
            is Outcome.Failure -> {
                val error = r.error
                if (error is AppError.Api && error.code == "VERSION_CONFLICT") refreshBaby(current.id)
                else purgeIfAccessLost(error, current.id)
                r
            }
        }
    }

    override suspend fun referenceData(): Outcome<ReferenceData> =
        apiCall { api.referenceData() }.map { it.toDomain() }

    /** Remove do cache um bebê cujo acesso foi perdido (SEC-005, INV-14). */
    private suspend fun purgeIfAccessLost(error: AppError, babyId: String) {
        if (error is AppError.Api && (error.code == "ACCESS_REVOKED" || error.httpStatus == 404)) {
            babyDao.delete(babyId)
            membershipDao.deleteForBaby(babyId)
        }
    }

    internal companion object {
        /**
         * Merge-patch (RFC 7396) só com os campos alterados. `due_date`/`sex` removidos viram `null` explícito.
         */
        fun buildMergePatch(current: Baby, draft: BabyDraft): JsonObject {
            val fields = linkedMapOf<String, kotlinx.serialization.json.JsonElement>()
            if (draft.displayName.trim() != current.displayName) fields["display_name"] = JsonPrimitive(draft.displayName.trim())
            if (draft.birthDate != current.birthDate) fields["birth_date"] = JsonPrimitive(draft.birthDate.toString())
            if (draft.dueDate != current.dueDate) {
                fields["due_date"] = draft.dueDate?.let { JsonPrimitive(it.toString()) } ?: JsonNull
            }
            if (draft.sex != current.sex) fields["sex"] = draft.sex?.let { JsonPrimitive(it.name) } ?: JsonNull
            if (draft.timezone != current.timezone) fields["timezone"] = JsonPrimitive(draft.timezone)
            return JsonObject(fields)
        }
    }
}
