package app.nina.ui.invite

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.nina.domain.model.AppError
import app.nina.domain.model.InvitationPreview
import app.nina.domain.model.Outcome
import app.nina.domain.repository.BabyRepository
import app.nina.domain.repository.CaregiverRepository
import app.nina.ui.FormIssue
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class InviteAcceptUiState(
    val input: String = "",
    val inputIssue: FormIssue? = null,
    val preview: InvitationPreview? = null,
    val busy: Boolean = false,
    val error: AppError? = null,
)

sealed interface InviteAcceptEvent {
    data object Accepted : InviteAcceptEvent
    data object Declined : InviteAcceptEvent
}

/** Convidado: cola o código/link, vê a prévia (sem nome completo do bebê) e aceita ou recusa (RF-006/007). */
class InviteAcceptViewModel(
    private val caregivers: CaregiverRepository,
    private val babies: BabyRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(InviteAcceptUiState())
    val state: StateFlow<InviteAcceptUiState> = _state.asStateFlow()

    private val _events = Channel<InviteAcceptEvent>(Channel.BUFFERED)
    val events = _events.receiveAsFlow()

    fun onInput(value: String) = _state.update { it.copy(input = value, inputIssue = null, preview = null, error = null) }

    fun inspect() {
        val token = extractToken(_state.value.input)
        if (token.isEmpty()) {
            _state.update { it.copy(inputIssue = FormIssue.REQUIRED) }
            return
        }
        viewModelScope.launch {
            _state.update { it.copy(busy = true, error = null) }
            when (val r = caregivers.inspectInvitation(token)) {
                is Outcome.Success -> _state.update { it.copy(busy = false, preview = r.value) }
                is Outcome.Failure -> _state.update { it.copy(busy = false, error = r.error) }
            }
        }
    }

    fun accept() = decide { token ->
        when (val r = caregivers.acceptInvitation(token)) {
            is Outcome.Success -> {
                babies.refreshBabies()
                _events.send(InviteAcceptEvent.Accepted)
                null
            }
            is Outcome.Failure -> r.error
        }
    }

    fun decline() = decide { token ->
        when (val r = caregivers.declineInvitation(token)) {
            is Outcome.Success -> {
                _events.send(InviteAcceptEvent.Declined)
                null
            }
            is Outcome.Failure -> r.error
        }
    }

    private fun decide(call: suspend (String) -> AppError?) {
        if (_state.value.busy || _state.value.preview == null) return
        val token = extractToken(_state.value.input)
        viewModelScope.launch {
            _state.update { it.copy(busy = true, error = null) }
            val error = call(token)
            _state.update { it.copy(busy = false, error = error) }
        }
    }

    companion object {
        /** Aceita o token puro ou um link com `token=<valor>`. */
        fun extractToken(input: String): String {
            val trimmed = input.trim()
            val marker = "token="
            val idx = trimmed.indexOf(marker)
            if (idx < 0) return trimmed
            return trimmed.substring(idx + marker.length).takeWhile { it != '&' && it != '#' && !it.isWhitespace() }
        }
    }
}
