package app.nina.ui.auth

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.nina.domain.model.AppError
import app.nina.domain.model.Outcome
import app.nina.domain.repository.AuthRepository
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class VerifyUiState(
    val email: String,
    val code: String = "",
    val submitting: Boolean = false,
    val resendInSeconds: Int = 0,
    val resent: Boolean = false,
    val error: AppError? = null,
)

/**
 * Confirmação de e-mail (ADR-0009): `POST /auth/email/verify` abre a primeira sessão; a navegação para a Home
 * acontece quando [AuthRepository.sessionState] vira SignedIn.
 */
class VerifyEmailViewModel(
    private val email: String,
    initialResendSeconds: Int?,
    private val auth: AuthRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(VerifyUiState(email = email))
    val state: StateFlow<VerifyUiState> = _state.asStateFlow()
    private var countdown: Job? = null

    init {
        startCountdown(initialResendSeconds ?: DEFAULT_RESEND_SECONDS)
    }

    fun onCode(value: String) = _state.update { it.copy(code = value.filter { c -> !c.isWhitespace() }, error = null) }

    fun submit() {
        val s = _state.value
        if (s.submitting || s.code.isBlank()) {
            if (s.code.isBlank()) _state.update { it.copy(error = AppError.Api(400, "VALIDATION_FAILED")) }
            return
        }
        viewModelScope.launch {
            _state.update { it.copy(submitting = true, error = null) }
            when (val r = auth.verifyEmail(email, s.code)) {
                is Outcome.Success -> _state.update { it.copy(submitting = false) }
                is Outcome.Failure -> _state.update { it.copy(submitting = false, error = r.error) }
            }
        }
    }

    fun resend() {
        if (_state.value.resendInSeconds > 0 || _state.value.submitting) return
        viewModelScope.launch {
            _state.update { it.copy(error = null, resent = false) }
            when (val r = auth.resendVerification()) {
                is Outcome.Success -> {
                    _state.update { it.copy(resent = true) }
                    startCountdown(r.value.resendAfterSeconds ?: DEFAULT_RESEND_SECONDS)
                }
                is Outcome.Failure -> _state.update { it.copy(error = r.error) }
            }
        }
    }

    private fun startCountdown(seconds: Int) {
        countdown?.cancel()
        _state.update { it.copy(resendInSeconds = seconds) }
        countdown = viewModelScope.launch {
            while (_state.value.resendInSeconds > 0) {
                delay(1_000)
                _state.update { it.copy(resendInSeconds = (it.resendInSeconds - 1).coerceAtLeast(0)) }
            }
        }
    }

    private companion object {
        const val DEFAULT_RESEND_SECONDS = 60
    }
}
