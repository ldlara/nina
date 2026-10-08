package app.nina.domain.model

import java.time.Instant
import java.time.LocalDate

data class User(
    val id: String,
    val email: String,
    val emailVerified: Boolean,
    val displayName: String?,
    val locale: String,
    val timezone: String?,
    val status: UserStatus,
    val hasPassword: Boolean,
)

/**
 * Cálculo canônico de idade vindo da API (ADR-0009). O app nunca calcula nem persiste idade corrigida como dado
 * do bebê: apenas exibe o que o servidor devolveu. `correctedDays == null` = não se aplica (nunca 0).
 */
data class AgeCalculation(
    val chronologicalDays: Int,
    val correctedDays: Int?,
    val correctionApplied: Boolean,
)

data class Baby(
    val id: String,
    val displayName: String,
    val birthDate: LocalDate,
    val dueDate: LocalDate?,
    val sex: Sex?,
    val timezone: String,
    val myRole: Role,
    val version: Int,
    /** Último valor recebido do servidor; pode ser nulo se o servidor não o enviou. */
    val ageCalculation: AgeCalculation?,
    /** Data local do bebê a que se refere [ageCalculation] (`age.as_of`), se informada. */
    val ageAsOf: LocalDate?,
    val createdAt: Instant,
    val updatedAt: Instant,
) {
    val canEdit: Boolean get() = myRole == Role.OWNER
}

data class Membership(
    val id: String,
    val babyId: String,
    val userId: String?,
    val userDisplayName: String?,
    val invitedEmail: String?,
    val role: Role,
    val status: MembershipStatus,
    val invitedAt: Instant,
    val invitationExpiresAt: Instant?,
    val acceptedAt: Instant?,
)

data class LegalDocument(
    val purpose: ConsentPurpose,
    val version: String,
    val url: String,
    val required: Boolean,
)

data class ConsentAcceptance(val purpose: ConsentPurpose, val documentVersion: String)

data class GuardianConsentStatus(val alreadyGranted: Boolean, val documentVersion: String?)

data class InvitationPreview(
    val inviterDisplayName: String,
    val babyLabel: String,
    val role: InvitableRole,
    val expiresAt: Instant,
)

/** Subconjunto de `/reference-data` usado pelo app nesta etapa. */
data class ReferenceData(
    val version: String,
    val correctedAgeMaxMonths: Int?,
    val invitationTtlDays: Int?,
    val diaperTypes: List<DiaperType>,
    val feedingTypes: List<FeedingType>,
    val milkTypes: List<MilkType>,
)

data class PendingVerification(val resendAfterSeconds: Int?)

/** Dados de um bebê a criar/editar, na forma de formulário. */
data class BabyDraft(
    val displayName: String,
    val birthDate: LocalDate,
    val dueDate: LocalDate?,
    val sex: Sex?,
    val timezone: String,
)

data class SessionUser(val id: String, val email: String, val displayName: String?)

sealed interface SessionState {
    data class SignedOut(val reason: SignedOutReason = SignedOutReason.NONE) : SessionState
    data class SignedIn(val user: SessionUser) : SessionState
}

enum class SignedOutReason { NONE, EXPIRED }
