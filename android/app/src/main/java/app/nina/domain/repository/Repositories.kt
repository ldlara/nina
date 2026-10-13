package app.nina.domain.repository

import app.nina.domain.auth.SocialCredential
import app.nina.domain.model.Baby
import app.nina.domain.model.BabyDraft
import app.nina.domain.model.ConsentAcceptance
import app.nina.domain.model.GuardianConsentStatus
import app.nina.domain.model.InvitableRole
import app.nina.domain.model.InvitationPreview
import app.nina.domain.model.LegalDocument
import app.nina.domain.model.Membership
import app.nina.domain.model.Outcome
import app.nina.domain.model.PendingVerification
import app.nina.domain.model.ReferenceData
import app.nina.domain.model.SessionState
import app.nina.domain.model.User
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.StateFlow

interface AuthRepository {
    val sessionState: StateFlow<SessionState>

    suspend fun loadLegalDocuments(): Outcome<List<LegalDocument>>

    /** Cadastro por e-mail: 202 uniforme; a sessão só nasce em [verifyEmail] (ADR-0009). */
    suspend fun register(
        email: String,
        password: String,
        displayName: String?,
        consents: List<ConsentAcceptance>,
    ): Outcome<PendingVerification>

    /** Reenvia o código repetindo o cadastro pendente (não há endpoint de reenvio no contrato v1). */
    suspend fun resendVerification(): Outcome<PendingVerification>

    suspend fun verifyEmail(email: String, code: String): Outcome<User>
    suspend fun login(email: String, password: String): Outcome<User>
    suspend fun socialLogin(credential: SocialCredential, consents: List<ConsentAcceptance>): Outcome<User>
    suspend fun logout()

    suspend fun guardianConsentStatus(): Outcome<GuardianConsentStatus>
    suspend fun grantGuardianConsent(documentVersion: String): Outcome<Unit>
}

interface BabyRepository {
    /** Lista local (Room), fonte da UI. */
    fun observeBabies(): Flow<List<Baby>>
    fun observeBaby(id: String): Flow<Baby?>

    /** Atualiza o cache a partir da API. Falha de rede mantém o cache. */
    suspend fun refreshBabies(): Outcome<Unit>
    suspend fun refreshBaby(id: String): Outcome<Baby>
    suspend fun createBaby(draft: BabyDraft): Outcome<Baby>
    suspend fun updateBaby(current: Baby, draft: BabyDraft): Outcome<Baby>
    suspend fun referenceData(): Outcome<ReferenceData>
}

interface CaregiverRepository {
    fun observeMembers(babyId: String): Flow<List<Membership>>
    suspend fun refresh(babyId: String): Outcome<Unit>
    suspend fun invite(babyId: String, email: String, role: InvitableRole): Outcome<Membership>
    suspend fun resend(babyId: String, membershipId: String): Outcome<Membership>
    suspend fun changeRole(babyId: String, membershipId: String, role: InvitableRole): Outcome<Membership>

    /** Remove cuidador, cancela convite pendente ou, se for o próprio vínculo, sai do bebê. */
    suspend fun remove(babyId: String, membershipId: String): Outcome<Unit>

    suspend fun inspectInvitation(token: String): Outcome<InvitationPreview>
    suspend fun acceptInvitation(token: String): Outcome<Baby>
    suspend fun declineInvitation(token: String): Outcome<Unit>
}
