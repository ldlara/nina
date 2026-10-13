package app.nina.data.repository

import app.nina.data.local.BabyDao
import app.nina.data.local.MembershipDao
import app.nina.data.remote.NinaApi
import app.nina.data.remote.apiCall
import app.nina.data.remote.dto.InvitationCreateDto
import app.nina.data.remote.dto.InvitationTokenDto
import app.nina.data.remote.dto.RoleChangeDto
import app.nina.domain.model.AppError
import app.nina.domain.model.Baby
import app.nina.domain.model.InvitableRole
import app.nina.domain.model.InvitationPreview
import app.nina.domain.model.Membership
import app.nina.domain.model.Outcome
import app.nina.domain.model.map
import app.nina.domain.repository.CaregiverRepository
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import java.util.UUID

class DefaultCaregiverRepository(
    private val api: NinaApi,
    private val membershipDao: MembershipDao,
    private val babyDao: BabyDao,
    private val newId: () -> String = { UUID.randomUUID().toString() },
) : CaregiverRepository {

    override fun observeMembers(babyId: String): Flow<List<Membership>> =
        membershipDao.observe(babyId).map { list -> list.map { it.toDomain() } }

    override suspend fun refresh(babyId: String): Outcome<Unit> =
        when (val r = apiCall { api.listCaregivers(babyId) }) {
            is Outcome.Success -> {
                membershipDao.replaceForBaby(babyId, r.value.items.map { it.toDomain().toEntity() })
                Outcome.Success(Unit)
            }
            is Outcome.Failure -> {
                val e = r.error
                if (e is AppError.Api && (e.code == "ACCESS_REVOKED" || e.httpStatus == 404)) {
                    // Vínculo revogado: apaga dados locais do bebê (SEC-005).
                    babyDao.delete(babyId)
                    membershipDao.deleteForBaby(babyId)
                }
                r
            }
        }

    override suspend fun invite(babyId: String, email: String, role: InvitableRole): Outcome<Membership> =
        apiCall { api.createInvitation(babyId, newId(), InvitationCreateDto(email.trim(), role.requireSendable())) }
            .map { it.toDomain().also { m -> membershipDao.upsert(m.toEntity()) } }

    override suspend fun resend(babyId: String, membershipId: String): Outcome<Membership> =
        apiCall { api.resendInvitation(babyId, membershipId) }
            .map { it.toDomain().also { m -> membershipDao.upsert(m.toEntity()) } }

    override suspend fun changeRole(babyId: String, membershipId: String, role: InvitableRole): Outcome<Membership> =
        apiCall { api.updateCaregiverRole(babyId, membershipId, RoleChangeDto(role.requireSendable())) }
            .map { it.toDomain().also { m -> membershipDao.upsert(m.toEntity()) } }

    override suspend fun remove(babyId: String, membershipId: String): Outcome<Unit> {
        val result = apiCall { api.removeCaregiver(babyId, membershipId) }
        if (result is Outcome.Success) membershipDao.delete(membershipId)
        return result
    }

    override suspend fun inspectInvitation(token: String): Outcome<InvitationPreview> =
        apiCall { api.inspectInvitation(InvitationTokenDto(token)) }.map { it.toDomain() }

    override suspend fun acceptInvitation(token: String): Outcome<Baby> =
        apiCall { api.acceptInvitation(InvitationTokenDto(token)) }
            .map { dto -> dto.toDomain().also { babyDao.upsert(it.toEntity()) } }

    override suspend fun declineInvitation(token: String): Outcome<Unit> =
        apiCall { api.declineInvitation(InvitationTokenDto(token)) }
}
