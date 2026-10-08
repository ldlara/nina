package app.nina.testutil

import app.nina.domain.auth.SocialAuthProvider
import app.nina.domain.auth.SocialAuthResult
import app.nina.domain.auth.SocialCredential
import app.nina.domain.model.AgeCalculation
import app.nina.domain.model.Baby
import app.nina.domain.model.BabyDraft
import app.nina.domain.model.ConsentAcceptance
import app.nina.domain.model.ConsentPurpose
import app.nina.domain.model.GuardianConsentStatus
import app.nina.domain.model.IdentityProvider
import app.nina.domain.model.InvitableRole
import app.nina.domain.model.InvitationPreview
import app.nina.domain.model.LegalDocument
import app.nina.domain.model.Membership
import app.nina.domain.model.MembershipStatus
import app.nina.domain.model.Outcome
import app.nina.domain.model.PendingVerification
import app.nina.domain.model.ReferenceData
import app.nina.domain.model.Role
import app.nina.domain.model.SessionState
import app.nina.domain.model.SessionUser
import app.nina.domain.model.Sex
import app.nina.domain.model.SignedOutReason
import app.nina.domain.model.User
import app.nina.domain.model.UserStatus
import app.nina.domain.repository.AuthRepository
import app.nina.domain.repository.BabyRepository
import app.nina.domain.repository.CaregiverRepository
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.map
import java.time.Instant
import java.time.LocalDate

fun testUser(id: String = "u-1") = User(id, "ana@example.org", true, "Ana", "pt-BR", "America/Sao_Paulo", UserStatus.ACTIVE, true)

fun testBaby(
    id: String = "b-1",
    role: Role = Role.OWNER,
    name: String = "Nina",
    version: Int = 3,
    dueDate: LocalDate? = LocalDate.of(2026, 1, 24),
    correctedDays: Int? = 257,
) = Baby(
    id = id, displayName = name, birthDate = LocalDate.of(2026, 1, 10), dueDate = dueDate, sex = null,
    timezone = "America/Sao_Paulo", myRole = role, version = version,
    ageCalculation = AgeCalculation(271, correctedDays, correctedDays != null),
    ageAsOf = LocalDate.of(2026, 10, 8), createdAt = Instant.EPOCH, updatedAt = Instant.EPOCH,
)

fun testMember(
    id: String,
    userId: String? = "u-$id",
    name: String? = "Pessoa $id",
    role: Role = Role.CAREGIVER,
    status: MembershipStatus = MembershipStatus.ACTIVE,
    email: String? = null,
) = Membership(id, "b-1", userId, name, email, role, status, Instant.EPOCH, null, null)

class FakeAuthRepository : AuthRepository {
    override val sessionState = MutableStateFlow<SessionState>(SessionState.SignedOut(SignedOutReason.NONE))

    var legal: Outcome<List<LegalDocument>> = Outcome.Success(
        listOf(
            LegalDocument(ConsentPurpose.TERMS_OF_USE, "1.0.0", "https://example.invalid/t", true),
            LegalDocument(ConsentPurpose.PRIVACY_POLICY, "1.1.0", "https://example.invalid/p", true),
            LegalDocument(ConsentPurpose.ANALYTICS_PRODUCT, "1.0.0", "https://example.invalid/a", false),
        ),
    )
    var registerResult: Outcome<PendingVerification> = Outcome.Success(PendingVerification(45))
    var resendResult: Outcome<PendingVerification> = Outcome.Success(PendingVerification(60))
    var verifyResult: Outcome<User> = Outcome.Success(testUser())
    var loginResult: Outcome<User> = Outcome.Success(testUser())
    var socialResult: Outcome<User> = Outcome.Success(testUser())
    var guardianStatus: Outcome<GuardianConsentStatus> = Outcome.Success(GuardianConsentStatus(false, "1.0.0"))
    var grantResult: Outcome<Unit> = Outcome.Success(Unit)

    val registerCalls = mutableListOf<List<ConsentAcceptance>>()
    val socialCalls = mutableListOf<Pair<SocialCredential, List<ConsentAcceptance>>>()
    var loginCalls = 0
    var resendCalls = 0
    var logoutCalls = 0
    val grantedVersions = mutableListOf<String>()

    override suspend fun loadLegalDocuments() = legal
    override suspend fun register(email: String, password: String, displayName: String?, consents: List<ConsentAcceptance>): Outcome<PendingVerification> {
        registerCalls += consents
        return registerResult
    }
    override suspend fun resendVerification(): Outcome<PendingVerification> { resendCalls++; return resendResult }
    override suspend fun verifyEmail(email: String, code: String): Outcome<User> = verifyResult.also {
        if (it is Outcome.Success) sessionState.value = SessionState.SignedIn(SessionUser("u-1", email, "Ana"))
    }
    override suspend fun login(email: String, password: String): Outcome<User> { loginCalls++; return loginResult }
    override suspend fun socialLogin(credential: SocialCredential, consents: List<ConsentAcceptance>): Outcome<User> {
        socialCalls += credential to consents
        return socialResult
    }
    override suspend fun logout() { logoutCalls++ }
    override suspend fun guardianConsentStatus() = guardianStatus
    override suspend fun grantGuardianConsent(documentVersion: String): Outcome<Unit> {
        grantedVersions += documentVersion
        return grantResult
    }
}

class FakeSocialProvider(var result: SocialAuthResult = SocialAuthResult.Unavailable) : SocialAuthProvider {
    val requested = mutableListOf<IdentityProvider>()
    override suspend fun authenticate(provider: IdentityProvider, nonce: String): SocialAuthResult {
        requested += provider
        return result
    }
}

class FakeBabyRepository : BabyRepository {
    val babies = MutableStateFlow<List<Baby>>(emptyList())
    var refreshBabiesResult: Outcome<Unit> = Outcome.Success(Unit)
    var refreshBabyResult: Outcome<Baby>? = null
    var createResult: Outcome<Baby>? = null
    var updateResult: Outcome<Baby>? = null
    var reference: Outcome<ReferenceData> = Outcome.Success(ReferenceData("1", 24, 7, emptyList(), emptyList(), emptyList()))
    val createdDrafts = mutableListOf<BabyDraft>()
    val updatedDrafts = mutableListOf<Pair<Baby, BabyDraft>>()
    var refreshBabiesCalls = 0

    override fun observeBabies(): Flow<List<Baby>> = babies
    override fun observeBaby(id: String): Flow<Baby?> = babies.map { l -> l.firstOrNull { it.id == id } }
    override suspend fun refreshBabies(): Outcome<Unit> { refreshBabiesCalls++; return refreshBabiesResult }
    override suspend fun refreshBaby(id: String): Outcome<Baby> = refreshBabyResult
        ?: babies.value.firstOrNull { it.id == id }?.let { Outcome.Success(it) }
        ?: Outcome.Failure(app.nina.domain.model.AppError.Network)
    override suspend fun createBaby(draft: BabyDraft): Outcome<Baby> {
        createdDrafts += draft
        return createResult ?: Outcome.Success(testBaby(name = draft.displayName))
    }
    override suspend fun updateBaby(current: Baby, draft: BabyDraft): Outcome<Baby> {
        updatedDrafts += current to draft
        return updateResult ?: Outcome.Success(current.copy(displayName = draft.displayName, version = current.version + 1))
    }
    override suspend fun referenceData() = reference
}

class FakeCaregiverRepository : CaregiverRepository {
    val members = MutableStateFlow<List<Membership>>(emptyList())
    var refreshResult: Outcome<Unit> = Outcome.Success(Unit)
    var inviteResult: Outcome<Membership>? = null
    var removeResult: Outcome<Unit> = Outcome.Success(Unit)
    var resendResult: Outcome<Membership>? = null
    var changeRoleResult: Outcome<Membership>? = null
    var inspectResult: Outcome<InvitationPreview> =
        Outcome.Success(InvitationPreview("Ana", "N.", InvitableRole.CAREGIVER, Instant.EPOCH))
    var acceptResult: Outcome<Baby> = Outcome.Success(testBaby(role = Role.CAREGIVER))
    var declineResult: Outcome<Unit> = Outcome.Success(Unit)

    val invites = mutableListOf<Pair<String, InvitableRole>>()
    val removed = mutableListOf<String>()
    val roleChanges = mutableListOf<Pair<String, InvitableRole>>()
    val resent = mutableListOf<String>()
    val inspectedTokens = mutableListOf<String>()
    val acceptedTokens = mutableListOf<String>()

    override fun observeMembers(babyId: String): Flow<List<Membership>> = members
    override suspend fun refresh(babyId: String) = refreshResult
    override suspend fun invite(babyId: String, email: String, role: InvitableRole): Outcome<Membership> {
        invites += email to role
        return inviteResult ?: Outcome.Success(testMember("new", userId = null, name = null, email = email, status = MembershipStatus.PENDING))
    }
    override suspend fun resend(babyId: String, membershipId: String): Outcome<Membership> {
        resent += membershipId
        return resendResult ?: Outcome.Success(testMember(membershipId))
    }
    override suspend fun changeRole(babyId: String, membershipId: String, role: InvitableRole): Outcome<Membership> {
        roleChanges += membershipId to role
        return changeRoleResult ?: Outcome.Success(testMember(membershipId))
    }
    override suspend fun remove(babyId: String, membershipId: String): Outcome<Unit> {
        removed += membershipId
        return removeResult
    }
    override suspend fun inspectInvitation(token: String): Outcome<InvitationPreview> { inspectedTokens += token; return inspectResult }
    override suspend fun acceptInvitation(token: String): Outcome<Baby> { acceptedTokens += token; return acceptResult }
    override suspend fun declineInvitation(token: String) = declineResult
}
