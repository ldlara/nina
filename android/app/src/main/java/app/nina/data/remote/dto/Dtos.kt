package app.nina.data.remote.dto

import app.nina.domain.model.ConsentPurpose
import app.nina.domain.model.ConsentSource
import app.nina.domain.model.ConsentStatus
import app.nina.domain.model.DiaperType
import app.nina.domain.model.FeedingType
import app.nina.domain.model.IdentityProvider
import app.nina.domain.model.InvitableRole
import app.nina.domain.model.MembershipStatus
import app.nina.domain.model.MilkType
import app.nina.domain.model.Platform
import app.nina.domain.model.Role
import app.nina.domain.model.Sex
import app.nina.domain.model.UserStatus
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/*
 * Modelos do contrato `contracts/openapi.yaml` v1.0.0 (escritos à mão). Nomes em snake_case por @SerialName;
 * enums em MAIÚSCULAS via serializers tolerantes (domain/model/Enums.kt). Campos desconhecidos são ignorados pelo Json
 * (ver NinaJson). Só os schemas de auth, baby, caregivers, consents, legal e reference-data estão modelados.
 */

// ------------------------------------------------------------------ common
@Serializable
data class ProblemDto(
    val type: String? = null,
    val title: String? = null,
    val status: Int? = null,
    val detail: String? = null,
    val code: String? = null,
    @SerialName("request_id") val requestId: String? = null,
    val errors: List<FieldErrorDto> = emptyList(),
    @SerialName("retry_after_seconds") val retryAfterSeconds: Int? = null,
)

@Serializable
data class FieldErrorDto(val field: String, val code: String)

// -------------------------------------------------------------------- auth
@Serializable
data class DeviceInfoDto(
    @SerialName("device_id") val deviceId: String,
    val platform: Platform,
    @SerialName("device_label") val deviceLabel: String? = null,
    @SerialName("app_version") val appVersion: String? = null,
    @SerialName("os_version") val osVersion: String? = null,
)

@Serializable
data class ConsentAcceptanceDto(
    @SerialName("purpose_key") val purposeKey: ConsentPurpose,
    @SerialName("document_version") val documentVersion: String,
)

@Serializable
data class RegisterRequestDto(
    val email: String,
    val password: String,
    @SerialName("display_name") val displayName: String? = null,
    val locale: String,
    val timezone: String? = null,
    val consents: List<ConsentAcceptanceDto>,
)

@Serializable
data class VerificationPendingDto(
    val status: String? = null,
    @SerialName("resend_after_seconds") val resendAfterSeconds: Int? = null,
)

@Serializable
data class EmailVerifyRequestDto(val email: String, val code: String, val device: DeviceInfoDto)

@Serializable
data class LoginRequestDto(val email: String, val password: String, val device: DeviceInfoDto)

@Serializable
data class SocialLoginRequestDto(
    @SerialName("id_token") val idToken: String,
    val nonce: String,
    val device: DeviceInfoDto,
    @SerialName("given_name") val givenName: String? = null,
    @SerialName("family_name") val familyName: String? = null,
    val locale: String? = null,
    val timezone: String? = null,
    val consents: List<ConsentAcceptanceDto>? = null,
)

@Serializable
data class RefreshRequestDto(
    @SerialName("refresh_token") val refreshToken: String,
    @SerialName("device_id") val deviceId: String,
)

@Serializable
data class IdentityDto(val provider: IdentityProvider, @SerialName("linked_at") val linkedAt: String? = null)

@Serializable
data class UserDto(
    val id: String,
    val email: String,
    @SerialName("email_verified") val emailVerified: Boolean = false,
    @SerialName("display_name") val displayName: String? = null,
    val locale: String = "pt-BR",
    val timezone: String? = null,
    val status: UserStatus = UserStatus.ACTIVE,
    @SerialName("has_password") val hasPassword: Boolean = false,
    val identities: List<IdentityDto> = emptyList(),
    @SerialName("created_at") val createdAt: String? = null,
)

@Serializable
data class TokenResponseDto(
    @SerialName("token_type") val tokenType: String? = null,
    @SerialName("access_token") val accessToken: String,
    @SerialName("expires_in") val expiresIn: Int,
    @SerialName("refresh_token") val refreshToken: String,
    @SerialName("refresh_expires_at") val refreshExpiresAt: String,
    @SerialName("session_id") val sessionId: String? = null,
    val user: UserDto,
    @SerialName("pending_consents") val pendingConsents: List<ConsentAcceptanceDto> = emptyList(),
)

// ------------------------------------------------------------------ babies
@Serializable
data class AgeValueDto(val days: Int, val weeks: Int, val months: Int)

@Serializable
data class AgeDto(
    @SerialName("as_of") val asOf: String? = null,
    val chronological: AgeValueDto? = null,
    val corrected: AgeValueDto? = null,
)

@Serializable
data class AgeCalculationDto(
    @SerialName("chronological_days") val chronologicalDays: Int,
    /** `null` = correção não se aplica (ADR-0009). */
    @SerialName("corrected_days") val correctedDays: Int? = null,
    @SerialName("correction_applied") val correctionApplied: Boolean = false,
)

@Serializable
data class BabyDto(
    val id: String,
    @SerialName("display_name") val displayName: String,
    @SerialName("birth_date") val birthDate: String,
    @SerialName("due_date") val dueDate: String? = null,
    val sex: Sex? = null,
    val timezone: String,
    @SerialName("my_role") val myRole: Role,
    val age: AgeDto? = null,
    @SerialName("age_calculation") val ageCalculation: AgeCalculationDto? = null,
    val version: Int,
    @SerialName("created_at") val createdAt: String,
    @SerialName("updated_at") val updatedAt: String,
)

@Serializable
data class BabyListDto(val items: List<BabyDto>)

@Serializable
data class BabyCreateDto(
    val id: String? = null,
    @SerialName("display_name") val displayName: String,
    @SerialName("birth_date") val birthDate: String,
    @SerialName("due_date") val dueDate: String? = null,
    val sex: Sex? = null,
    val timezone: String? = null,
)

// -------------------------------------------------------------- caregivers
@Serializable
data class UserRefDto(val id: String, @SerialName("display_name") val displayName: String? = null)

@Serializable
data class MembershipDto(
    val id: String,
    @SerialName("baby_id") val babyId: String,
    val user: UserRefDto? = null,
    @SerialName("invited_email") val invitedEmail: String? = null,
    val role: Role,
    val status: MembershipStatus,
    @SerialName("invited_at") val invitedAt: String,
    @SerialName("invitation_expires_at") val invitationExpiresAt: String? = null,
    @SerialName("accepted_at") val acceptedAt: String? = null,
)

@Serializable
data class MembershipListDto(val items: List<MembershipDto>)

@Serializable
data class InvitationCreateDto(val email: String, val role: InvitableRole)

@Serializable
data class RoleChangeDto(val role: InvitableRole)

@Serializable
data class InvitationTokenDto(val token: String)

@Serializable
data class InvitationPreviewDto(
    @SerialName("inviter_display_name") val inviterDisplayName: String,
    @SerialName("baby_label") val babyLabel: String,
    val role: InvitableRole,
    @SerialName("expires_at") val expiresAt: String,
    @SerialName("visible_data") val visibleData: List<String> = emptyList(),
)

// ---------------------------------------------------------------- privacy
@Serializable
data class LegalDocumentDto(
    @SerialName("purpose_key") val purposeKey: ConsentPurpose,
    val version: String,
    val url: String,
    @SerialName("content_hash") val contentHash: String? = null,
    @SerialName("effective_at") val effectiveAt: String? = null,
    val required: Boolean = false,
)

@Serializable
data class LegalDocumentListDto(val items: List<LegalDocumentDto>)

@Serializable
data class ConsentInputDto(
    @SerialName("purpose_key") val purposeKey: ConsentPurpose,
    @SerialName("document_version") val documentVersion: String,
    val status: ConsentStatus,
    val source: ConsentSource,
    @SerialName("baby_id") val babyId: String? = null,
    val locale: String? = null,
)

@Serializable
data class ConsentRecordDto(
    val id: String,
    @SerialName("purpose_key") val purposeKey: ConsentPurpose,
    @SerialName("document_version") val documentVersion: String,
    val status: ConsentStatus,
    @SerialName("granted_at") val grantedAt: String? = null,
    @SerialName("revoked_at") val revokedAt: String? = null,
    @SerialName("baby_id") val babyId: String? = null,
    val source: ConsentSource? = null,
)

@Serializable
data class ConsentsResponseDto(
    val current: List<ConsentRecordDto> = emptyList(),
    @SerialName("pending_required") val pendingRequired: List<LegalDocumentDto> = emptyList(),
)

// --------------------------------------------------------- reference data
@Serializable
data class PoliciesDto(
    @SerialName("corrected_age_max_months") val correctedAgeMaxMonths: Int? = null,
    @SerialName("deletion_grace_days") val deletionGraceDays: Int? = null,
)

@Serializable
data class LimitsDto(
    @SerialName("invitation_ttl_days") val invitationTtlDays: Int? = null,
    @SerialName("notes_max_length") val notesMaxLength: Int? = null,
)

@Serializable
data class ReferenceDataDto(
    val version: String,
    @SerialName("diaper_types") val diaperTypes: List<DiaperType> = emptyList(),
    @SerialName("feeding_types") val feedingTypes: List<FeedingType> = emptyList(),
    @SerialName("milk_types") val milkTypes: List<MilkType> = emptyList(),
    @SerialName("sleep_methods") val sleepMethods: List<String> = emptyList(),
    val policies: PoliciesDto = PoliciesDto(),
    val limits: LimitsDto = LimitsDto(),
)
