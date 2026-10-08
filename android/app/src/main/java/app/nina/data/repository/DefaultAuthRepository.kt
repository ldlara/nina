package app.nina.data.repository

import app.nina.data.remote.NinaApi
import app.nina.data.remote.apiCall
import app.nina.data.remote.dto.ConsentInputDto
import app.nina.data.remote.dto.DeviceInfoDto
import app.nina.data.remote.dto.EmailVerifyRequestDto
import app.nina.data.remote.dto.LoginRequestDto
import app.nina.data.remote.dto.RegisterRequestDto
import app.nina.data.remote.dto.SocialLoginRequestDto
import app.nina.data.remote.dto.TokenResponseDto
import app.nina.data.session.SessionManager
import app.nina.data.session.StoredSession
import app.nina.domain.auth.SocialCredential
import app.nina.domain.model.AppError
import app.nina.domain.model.ConsentAcceptance
import app.nina.domain.model.ConsentPurpose
import app.nina.domain.model.ConsentSource
import app.nina.domain.model.ConsentStatus
import app.nina.domain.model.GuardianConsentStatus
import app.nina.domain.model.IdentityProvider
import app.nina.domain.model.LegalDocument
import app.nina.domain.model.Outcome
import app.nina.domain.model.PendingVerification
import app.nina.domain.model.SessionState
import app.nina.domain.model.User
import app.nina.domain.model.map
import app.nina.domain.repository.AuthRepository
import kotlinx.coroutines.flow.StateFlow
import java.util.UUID

/** Informações do ambiente de execução, injetadas para permitir testes sem Android. */
data class Environment(
    val deviceInfo: () -> DeviceInfoDto,
    val localeTag: () -> String,
    val timezoneId: () -> String,
    val newId: () -> String = { UUID.randomUUID().toString() },
)

class DefaultAuthRepository(
    private val api: NinaApi,
    private val sessionManager: SessionManager,
    private val environment: Environment,
    /** Limpa o cache local (Room) no logout voluntário. */
    private val clearLocalData: suspend () -> Unit,
) : AuthRepository {

    override val sessionState: StateFlow<SessionState> get() = sessionManager.state

    /** Cadastro aguardando confirmação; só em memória (a senha nunca é persistida). */
    private var pendingRegistration: RegisterRequestDto? = null

    override suspend fun loadLegalDocuments(): Outcome<List<LegalDocument>> =
        apiCall { api.legalDocuments() }.map { list -> list.items.map { it.toDomain() } }

    override suspend fun register(
        email: String,
        password: String,
        displayName: String?,
        consents: List<ConsentAcceptance>,
    ): Outcome<PendingVerification> {
        val request = RegisterRequestDto(
            email = email.trim(),
            password = password,
            displayName = displayName?.trim()?.takeIf { it.isNotEmpty() },
            locale = environment.localeTag(),
            timezone = environment.timezoneId(),
            consents = consents.map { it.toDto() },
        )
        return submitRegistration(request)
    }

    override suspend fun resendVerification(): Outcome<PendingVerification> {
        val request = pendingRegistration
            ?: return Outcome.Failure(AppError.Unexpected("Nenhum cadastro pendente"))
        return submitRegistration(request)
    }

    private suspend fun submitRegistration(request: RegisterRequestDto): Outcome<PendingVerification> =
        apiCall { api.register(environment.newId(), request) }.map {
            pendingRegistration = request
            PendingVerification(it.resendAfterSeconds)
        }

    override suspend fun verifyEmail(email: String, code: String): Outcome<User> =
        apiCall { api.verifyEmail(EmailVerifyRequestDto(email.trim(), code.trim(), environment.deviceInfo())) }
            .map { tokens ->
                pendingRegistration = null
                persist(tokens)
            }

    override suspend fun login(email: String, password: String): Outcome<User> =
        apiCall { api.login(LoginRequestDto(email.trim(), password, environment.deviceInfo())) }.map(::persist)

    override suspend fun socialLogin(credential: SocialCredential, consents: List<ConsentAcceptance>): Outcome<User> {
        val body = SocialLoginRequestDto(
            idToken = credential.idToken,
            nonce = credential.nonce,
            device = environment.deviceInfo(),
            givenName = credential.givenName,
            familyName = credential.familyName,
            locale = environment.localeTag(),
            timezone = environment.timezoneId(),
            consents = consents.takeIf { it.isNotEmpty() }?.map { it.toDto() },
        )
        return when (credential.provider) {
            IdentityProvider.GOOGLE -> apiCall { api.loginWithGoogle(body) }
            IdentityProvider.APPLE -> apiCall { api.loginWithApple(body) }
            IdentityProvider.UNRECOGNIZED -> Outcome.Failure(AppError.Unexpected("Provedor inválido"))
        }.map(::persist)
    }

    override suspend fun logout() {
        if (sessionManager.accessToken() != null) {
            apiCall { api.logout() } // melhor esforço; a sessão local é encerrada de qualquer forma
        }
        sessionManager.clear()
        pendingRegistration = null
        clearLocalData()
    }

    override suspend fun guardianConsentStatus(): Outcome<GuardianConsentStatus> {
        val consents = when (val r = apiCall { api.consents() }) {
            is Outcome.Failure -> return r
            is Outcome.Success -> r.value
        }
        val granted = consents.current.any {
            it.purposeKey == ConsentPurpose.CHILD_DATA_GUARDIAN && it.status == ConsentStatus.GRANTED
        }
        if (granted) return Outcome.Success(GuardianConsentStatus(alreadyGranted = true, documentVersion = null))
        val pendingVersion = consents.pendingRequired
            .firstOrNull { it.purposeKey == ConsentPurpose.CHILD_DATA_GUARDIAN }?.version
        val version = pendingVersion ?: (loadLegalDocuments() as? Outcome.Success)?.value
            ?.firstOrNull { it.purpose == ConsentPurpose.CHILD_DATA_GUARDIAN }?.version
        return Outcome.Success(GuardianConsentStatus(alreadyGranted = false, documentVersion = version))
    }

    override suspend fun grantGuardianConsent(documentVersion: String): Outcome<Unit> =
        apiCall {
            api.recordConsent(
                ConsentInputDto(
                    purposeKey = ConsentPurpose.CHILD_DATA_GUARDIAN,
                    documentVersion = documentVersion,
                    status = ConsentStatus.GRANTED,
                    source = ConsentSource.ONBOARDING,
                    locale = environment.localeTag(),
                ),
            )
        }.map { }

    private fun persist(tokens: TokenResponseDto): User {
        sessionManager.save(
            StoredSession(
                accessToken = tokens.accessToken,
                refreshToken = tokens.refreshToken,
                refreshExpiresAt = tokens.refreshExpiresAt,
                userId = tokens.user.id,
                email = tokens.user.email,
                displayName = tokens.user.displayName,
            ),
        )
        return tokens.user.toDomain()
    }
}
