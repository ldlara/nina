package app.nina.ui.auth

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.nina.domain.auth.SocialAuthProvider
import app.nina.domain.auth.SocialAuthResult
import app.nina.domain.model.AppError
import app.nina.domain.model.ConsentAcceptance
import app.nina.domain.model.ConsentPurpose
import app.nina.domain.model.IdentityProvider
import app.nina.domain.model.LegalDocument
import app.nina.domain.model.Outcome
import app.nina.domain.model.apiCode
import app.nina.domain.repository.AuthRepository
import app.nina.ui.FormIssue
import app.nina.ui.fieldError
import app.nina.ui.isPlausibleEmail
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.util.UUID

enum class AuthMode { REGISTER, LOGIN }

sealed interface LegalState {
    data object Loading : LegalState
    data object Failed : LegalState
    data class Loaded(val documents: List<LegalDocument>) : LegalState
}

data class AuthUiState(
    val mode: AuthMode = AuthMode.REGISTER,
    val email: String = "",
    val password: String = "",
    val displayName: String = "",
    val showPassword: Boolean = false,
    val acceptTerms: Boolean = false,
    val acceptAnalytics: Boolean = false,
    val acceptMarketing: Boolean = false,
    val legal: LegalState = LegalState.Loading,
    val submitting: Boolean = false,
    val emailIssue: FormIssue? = null,
    val passwordIssue: FormIssue? = null,
    val termsMissing: Boolean = false,
    val error: AppError? = null,
) {
    val termsVersion: String?
        get() = (legal as? LegalState.Loaded)?.documents
            ?.firstOrNull { it.purpose == ConsentPurpose.TERMS_OF_USE }?.version
}

sealed interface AuthEvent {
    /** Cadastro aceito (202): ir para a confirmação de e-mail. A sessão só nasce depois (ADR-0009). */
    data class VerificationRequired(val email: String, val resendAfterSeconds: Int?) : AuthEvent
}

class AuthViewModel(
    private val auth: AuthRepository,
    private val social: SocialAuthProvider,
    private val newNonce: () -> String = { UUID.randomUUID().toString() },
) : ViewModel() {

    private val _state = MutableStateFlow(AuthUiState())
    val state: StateFlow<AuthUiState> = _state.asStateFlow()

    private val _events = Channel<AuthEvent>(Channel.BUFFERED)
    val events = _events.receiveAsFlow()

    init {
        loadLegal()
    }

    fun loadLegal() {
        _state.update { it.copy(legal = LegalState.Loading) }
        viewModelScope.launch {
            val legal = when (val r = auth.loadLegalDocuments()) {
                is Outcome.Success -> LegalState.Loaded(r.value)
                is Outcome.Failure -> LegalState.Failed
            }
            _state.update { it.copy(legal = legal) }
        }
    }

    fun setMode(mode: AuthMode) = _state.update {
        it.copy(mode = mode, emailIssue = null, passwordIssue = null, termsMissing = false, error = null)
    }

    fun onEmail(value: String) = _state.update { it.copy(email = value, emailIssue = null, error = null) }
    fun onPassword(value: String) = _state.update { it.copy(password = value, passwordIssue = null, error = null) }
    fun onDisplayName(value: String) = _state.update { it.copy(displayName = value) }
    fun toggleShowPassword() = _state.update { it.copy(showPassword = !it.showPassword) }
    fun onAcceptTerms(value: Boolean) = _state.update { it.copy(acceptTerms = value, termsMissing = false, error = null) }
    fun onAcceptAnalytics(value: Boolean) = _state.update { it.copy(acceptAnalytics = value) }
    fun onAcceptMarketing(value: Boolean) = _state.update { it.copy(acceptMarketing = value) }

    fun submit() {
        val s = _state.value
        if (s.submitting) return
        val emailIssue = when {
            s.email.isBlank() -> FormIssue.REQUIRED
            !isPlausibleEmail(s.email) -> FormIssue.INVALID_EMAIL
            else -> null
        }
        val passwordIssue = if (s.password.isEmpty()) FormIssue.REQUIRED else null
        val termsMissing = s.mode == AuthMode.REGISTER && !s.acceptTerms
        if (emailIssue != null || passwordIssue != null || termsMissing) {
            _state.update { it.copy(emailIssue = emailIssue, passwordIssue = passwordIssue, termsMissing = termsMissing) }
            return
        }
        when (s.mode) {
            AuthMode.LOGIN -> launchAuth { auth.login(s.email, s.password) }
            AuthMode.REGISTER -> {
                val consents = buildConsents(s)
                if (consents == null) {
                    _state.update { it.copy(error = AppError.Unexpected("legal documents unavailable")) }
                    return
                }
                viewModelScope.launch {
                    _state.update { it.copy(submitting = true, error = null) }
                    when (val r = auth.register(s.email, s.password, s.displayName, consents)) {
                        is Outcome.Success -> {
                            _state.update { it.copy(submitting = false) }
                            _events.send(AuthEvent.VerificationRequired(s.email.trim(), r.value.resendAfterSeconds))
                        }
                        is Outcome.Failure -> fail(r.error)
                    }
                }
            }
        }
    }

    fun onSocial(provider: IdentityProvider) {
        val s = _state.value
        if (s.submitting) return
        viewModelScope.launch {
            _state.update { it.copy(submitting = true, error = null) }
            when (val result = social.authenticate(provider, newNonce())) {
                SocialAuthResult.Unavailable -> fail(AppError.SocialUnavailable)
                SocialAuthResult.Cancelled -> _state.update { it.copy(submitting = false) }
                is SocialAuthResult.Success -> {
                    // Termos aceitos na tela seguem junto; se a conta já existe, o servidor ignora/aceita.
                    val consents = if (s.acceptTerms) buildConsents(s).orEmpty() else emptyList()
                    when (val r = auth.socialLogin(result.credential, consents)) {
                        is Outcome.Success -> _state.update { it.copy(submitting = false) } // sessão muda; navegação reage
                        is Outcome.Failure -> fail(r.error)
                    }
                }
            }
        }
    }

    private fun launchAuth(call: suspend () -> Outcome<*>) {
        viewModelScope.launch {
            _state.update { it.copy(submitting = true, error = null) }
            when (val r = call()) {
                is Outcome.Success -> _state.update { it.copy(submitting = false) }
                is Outcome.Failure -> fail(r.error)
            }
        }
    }

    private fun fail(error: AppError) = _state.update {
        it.copy(
            submitting = false,
            error = error,
            emailIssue = error.fieldError("email")?.let { f -> FormIssue.fromServer(f.code) },
            passwordIssue = error.fieldError("password")?.let { f -> FormIssue.fromServer(f.code) },
            termsMissing = error.apiCode == "CONSENT_REQUIRED",
        )
    }

    /** Termos e Política (obrigatórios) + opcionais marcados que tenham documento vigente. Nulo se não há documentos. */
    private fun buildConsents(s: AuthUiState): List<ConsentAcceptance>? {
        val docs = (s.legal as? LegalState.Loaded)?.documents ?: return null
        fun version(p: ConsentPurpose) = docs.firstOrNull { it.purpose == p }?.version
        val terms = version(ConsentPurpose.TERMS_OF_USE) ?: return null
        val privacy = version(ConsentPurpose.PRIVACY_POLICY) ?: return null
        return buildList {
            add(ConsentAcceptance(ConsentPurpose.TERMS_OF_USE, terms))
            add(ConsentAcceptance(ConsentPurpose.PRIVACY_POLICY, privacy))
            if (s.acceptAnalytics) version(ConsentPurpose.ANALYTICS_PRODUCT)?.let { add(ConsentAcceptance(ConsentPurpose.ANALYTICS_PRODUCT, it)) }
            if (s.acceptMarketing) version(ConsentPurpose.MARKETING_EMAIL)?.let { add(ConsentAcceptance(ConsentPurpose.MARKETING_EMAIL, it)) }
        }
    }
}
