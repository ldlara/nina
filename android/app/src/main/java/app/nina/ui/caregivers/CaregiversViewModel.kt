package app.nina.ui.caregivers

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import app.nina.domain.model.AppError
import app.nina.domain.model.InvitableRole
import app.nina.domain.model.Membership
import app.nina.domain.model.MembershipStatus
import app.nina.domain.model.Outcome
import app.nina.domain.model.Role
import app.nina.domain.model.SessionState
import app.nina.domain.repository.AuthRepository
import app.nina.domain.repository.BabyRepository
import app.nina.domain.repository.CaregiverRepository
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

sealed interface ConfirmAction {
    val member: Membership

    data class Remove(override val member: Membership) : ConfirmAction
    data class CancelInvite(override val member: Membership) : ConfirmAction
    data class Leave(override val member: Membership) : ConfirmAction
}

data class InviteForm(
    val email: String = "",
    val role: InvitableRole = InvitableRole.CAREGIVER,
    val emailIssue: FormIssue? = null,
    val sending: Boolean = false,
)

data class CaregiversUiState(
    val members: List<Membership> = emptyList(),
    val myRole: Role = Role.UNRECOGNIZED,
    val myUserId: String? = null,
    val babyName: String = "",
    val loading: Boolean = true,
    val error: AppError? = null,
    val invite: InviteForm? = null,
    val confirm: ConfirmAction? = null,
    val working: Boolean = false,
) {
    val isOwner: Boolean get() = myRole == Role.OWNER
    fun isMe(m: Membership): Boolean = myUserId != null && m.userId == myUserId
}

enum class CaregiversMessage { INVITE_SENT, INVITE_RESENT, REMOVED, ROLE_CHANGED }

sealed interface CaregiversEvent {
    data class Message(val message: CaregiversMessage) : CaregiversEvent

    /** O usuário saiu do bebê ou perdeu o acesso: voltar à lista. */
    data object LeftBaby : CaregiversEvent
}

class CaregiversViewModel(
    private val babyId: String,
    private val caregivers: CaregiverRepository,
    private val babies: BabyRepository,
    private val auth: AuthRepository,
) : ViewModel() {

    private val _state = MutableStateFlow(
        CaregiversUiState(myUserId = (auth.sessionState.value as? SessionState.SignedIn)?.user?.id),
    )
    val state: StateFlow<CaregiversUiState> = _state.asStateFlow()

    private val _events = Channel<CaregiversEvent>(Channel.BUFFERED)
    val events = _events.receiveAsFlow()

    init {
        viewModelScope.launch {
            caregivers.observeMembers(babyId).collect { list -> _state.update { it.copy(members = list) } }
        }
        viewModelScope.launch {
            babies.observeBaby(babyId).collect { baby ->
                if (baby != null) _state.update { it.copy(myRole = baby.myRole, babyName = baby.displayName) }
            }
        }
        refresh()
    }

    fun refresh() {
        viewModelScope.launch {
            _state.update { it.copy(loading = true, error = null) }
            when (val r = caregivers.refresh(babyId)) {
                is Outcome.Success -> _state.update { it.copy(loading = false) }
                is Outcome.Failure -> {
                    _state.update { it.copy(loading = false, error = r.error.takeUnless { e -> e == AppError.Network }) }
                    if (isAccessLost(r.error)) _events.send(CaregiversEvent.LeftBaby)
                }
            }
            babies.refreshBaby(babyId) // atualiza my_role; falha é ignorada (cache continua válido)
        }
    }

    // ---- Convite
    fun openInvite() = _state.update { it.copy(invite = InviteForm(), error = null) }
    fun closeInvite() = _state.update { it.copy(invite = null) }
    fun onInviteEmail(value: String) = _state.update { s -> s.copy(invite = s.invite?.copy(email = value, emailIssue = null)) }
    fun onInviteRole(role: InvitableRole) = _state.update { s -> s.copy(invite = s.invite?.copy(role = role)) }

    fun sendInvite() {
        val form = _state.value.invite ?: return
        if (form.sending) return
        val issue = when {
            form.email.isBlank() -> FormIssue.REQUIRED
            !isPlausibleEmail(form.email) -> FormIssue.INVALID_EMAIL
            else -> null
        }
        if (issue != null) {
            _state.update { it.copy(invite = form.copy(emailIssue = issue)) }
            return
        }
        viewModelScope.launch {
            _state.update { it.copy(invite = form.copy(sending = true), error = null) }
            when (val r = caregivers.invite(babyId, form.email, form.role)) {
                is Outcome.Success -> {
                    _state.update { it.copy(invite = null) }
                    _events.send(CaregiversEvent.Message(CaregiversMessage.INVITE_SENT))
                }
                is Outcome.Failure -> _state.update {
                    it.copy(
                        invite = form.copy(
                            sending = false,
                            emailIssue = r.error.fieldError("email")?.let { f -> FormIssue.fromServer(f.code) },
                        ),
                        error = r.error,
                    )
                }
            }
        }
    }

    // ---- Ações sobre membros
    fun resend(member: Membership) = act(CaregiversMessage.INVITE_RESENT) { caregivers.resend(babyId, member.id) }

    fun changeRole(member: Membership, role: InvitableRole) {
        if (member.role.name == role.name) return
        act(CaregiversMessage.ROLE_CHANGED) { caregivers.changeRole(babyId, member.id, role) }
    }

    fun ask(action: ConfirmAction) = _state.update { it.copy(confirm = action) }
    fun dismissConfirm() = _state.update { it.copy(confirm = null) }

    fun confirm() {
        val action = _state.value.confirm ?: return
        _state.update { it.copy(confirm = null, working = true, error = null) }
        viewModelScope.launch {
            when (val r = caregivers.remove(babyId, action.member.id)) {
                is Outcome.Success -> {
                    _state.update { it.copy(working = false) }
                    if (action is ConfirmAction.Leave) {
                        babies.refreshBabies() // o bebê some do cache se o servidor não o devolve mais
                        _events.send(CaregiversEvent.LeftBaby)
                    } else {
                        _events.send(CaregiversEvent.Message(CaregiversMessage.REMOVED))
                    }
                }
                is Outcome.Failure -> _state.update { it.copy(working = false, error = r.error) }
            }
        }
    }

    private fun act(message: CaregiversMessage, call: suspend () -> Outcome<*>) {
        if (_state.value.working) return
        viewModelScope.launch {
            _state.update { it.copy(working = true, error = null) }
            when (val r = call()) {
                is Outcome.Success -> {
                    _state.update { it.copy(working = false) }
                    _events.send(CaregiversEvent.Message(message))
                }
                is Outcome.Failure -> _state.update { it.copy(working = false, error = r.error) }
            }
        }
    }

    private fun isAccessLost(error: AppError) =
        error is AppError.Api && (error.code == "ACCESS_REVOKED" || error.httpStatus == 404)

    companion object {
        /** Pendentes e ativos são exibidos; vínculos encerrados (revogado/recusado/expirado) ficam fora da lista. */
        fun visible(members: List<Membership>): List<Membership> =
            members.filter { it.status == MembershipStatus.ACTIVE || it.status == MembershipStatus.PENDING }
    }
}
