package app.nina.data.remote

import app.nina.data.remote.dto.BabyCreateDto
import app.nina.data.remote.dto.BabyDto
import app.nina.data.remote.dto.BabyListDto
import app.nina.data.remote.dto.ConsentInputDto
import app.nina.data.remote.dto.ConsentRecordDto
import app.nina.data.remote.dto.ConsentsResponseDto
import app.nina.data.remote.dto.EmailVerifyRequestDto
import app.nina.data.remote.dto.InvitationCreateDto
import app.nina.data.remote.dto.InvitationPreviewDto
import app.nina.data.remote.dto.InvitationTokenDto
import app.nina.data.remote.dto.LegalDocumentListDto
import app.nina.data.remote.dto.LoginRequestDto
import app.nina.data.remote.dto.MembershipDto
import app.nina.data.remote.dto.MembershipListDto
import app.nina.data.remote.dto.RefreshRequestDto
import app.nina.data.remote.dto.ReferenceDataDto
import app.nina.data.remote.dto.RegisterRequestDto
import app.nina.data.remote.dto.RoleChangeDto
import app.nina.data.remote.dto.SocialLoginRequestDto
import app.nina.data.remote.dto.TokenResponseDto
import app.nina.data.remote.dto.UserDto
import app.nina.data.remote.dto.VerificationPendingDto
import okhttp3.RequestBody
import retrofit2.http.Body
import retrofit2.http.DELETE
import retrofit2.http.GET
import retrofit2.http.Header
import retrofit2.http.Headers
import retrofit2.http.PATCH
import retrofit2.http.POST
import retrofit2.http.Path

/** Cabeçalho interno (removido antes do envio) que marca endpoints `security: []` do contrato. */
const val NO_AUTH_HEADER = "X-Nina-No-Auth"

/** Operações de `contracts/openapi.yaml` v1.0.0 usadas pelo app. Caminhos relativos ao servidor `.../v1/`. */
interface NinaApi {
    // ---- Auth (públicas)
    @Headers("$NO_AUTH_HEADER: true")
    @POST("auth/register")
    suspend fun register(
        @Header("Idempotency-Key") idempotencyKey: String,
        @Body body: RegisterRequestDto,
    ): VerificationPendingDto

    @Headers("$NO_AUTH_HEADER: true")
    @POST("auth/email/verify")
    suspend fun verifyEmail(@Body body: EmailVerifyRequestDto): TokenResponseDto

    @Headers("$NO_AUTH_HEADER: true")
    @POST("auth/login")
    suspend fun login(@Body body: LoginRequestDto): TokenResponseDto

    @Headers("$NO_AUTH_HEADER: true")
    @POST("auth/google")
    suspend fun loginWithGoogle(@Body body: SocialLoginRequestDto): TokenResponseDto

    @Headers("$NO_AUTH_HEADER: true")
    @POST("auth/apple")
    suspend fun loginWithApple(@Body body: SocialLoginRequestDto): TokenResponseDto

    @Headers("$NO_AUTH_HEADER: true")
    @POST("auth/refresh")
    suspend fun refresh(@Body body: RefreshRequestDto): TokenResponseDto

    @POST("auth/logout")
    suspend fun logout()

    @Headers("$NO_AUTH_HEADER: true")
    @GET("legal/documents")
    suspend fun legalDocuments(): LegalDocumentListDto

    // ---- Conta / consentimentos
    @GET("me")
    suspend fun me(): UserDto

    @GET("me/consents")
    suspend fun consents(): ConsentsResponseDto

    @POST("me/consents")
    suspend fun recordConsent(@Body body: ConsentInputDto): ConsentRecordDto

    // ---- Bebês
    @GET("babies")
    suspend fun listBabies(): BabyListDto

    @POST("babies")
    suspend fun createBaby(
        @Header("Idempotency-Key") idempotencyKey: String,
        @Body body: BabyCreateDto,
    ): BabyDto

    @GET("babies/{baby_id}")
    suspend fun getBaby(@Path("baby_id") babyId: String): BabyDto

    /** Merge-patch (`application/merge-patch+json`); `If-Match` com `"<version>"`. */
    @PATCH("babies/{baby_id}")
    suspend fun updateBaby(
        @Path("baby_id") babyId: String,
        @Header("If-Match") ifMatch: String,
        @Body body: RequestBody,
    ): BabyDto

    // ---- Cuidadores e convites
    @GET("babies/{baby_id}/caregivers")
    suspend fun listCaregivers(@Path("baby_id") babyId: String): MembershipListDto

    @PATCH("babies/{baby_id}/caregivers/{membership_id}")
    suspend fun updateCaregiverRole(
        @Path("baby_id") babyId: String,
        @Path("membership_id") membershipId: String,
        @Body body: RoleChangeDto,
    ): MembershipDto

    @DELETE("babies/{baby_id}/caregivers/{membership_id}")
    suspend fun removeCaregiver(
        @Path("baby_id") babyId: String,
        @Path("membership_id") membershipId: String,
    )

    @POST("babies/{baby_id}/invitations")
    suspend fun createInvitation(
        @Path("baby_id") babyId: String,
        @Header("Idempotency-Key") idempotencyKey: String,
        @Body body: InvitationCreateDto,
    ): MembershipDto

    @POST("babies/{baby_id}/invitations/{membership_id}/resend")
    suspend fun resendInvitation(
        @Path("baby_id") babyId: String,
        @Path("membership_id") membershipId: String,
    ): MembershipDto

    @POST("invitations/inspect")
    suspend fun inspectInvitation(@Body body: InvitationTokenDto): InvitationPreviewDto

    @POST("invitations/accept")
    suspend fun acceptInvitation(@Body body: InvitationTokenDto): BabyDto

    @POST("invitations/decline")
    suspend fun declineInvitation(@Body body: InvitationTokenDto)

    // ---- Referência
    @GET("reference-data")
    suspend fun referenceData(): ReferenceDataDto
}
