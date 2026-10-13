package app.nina.ui.caregivers

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.nina.R
import app.nina.domain.model.InvitableRole
import app.nina.domain.model.Membership
import app.nina.domain.model.MembershipStatus
import app.nina.ui.components.ErrorBanner
import app.nina.ui.components.InfoBanner
import app.nina.ui.components.LoadingBox
import app.nina.ui.components.NinaCard
import app.nina.ui.components.NinaDestructiveTextButton
import app.nina.ui.components.NinaPrimaryButton
import app.nina.ui.components.NinaScreen
import app.nina.ui.components.NinaTextButton
import app.nina.ui.components.NinaTextField
import app.nina.ui.descriptionRes
import app.nina.ui.formatInstantDate
import app.nina.ui.labelRes
import app.nina.ui.messageRes
import app.nina.ui.theme.NinaDimens

@Composable
fun CaregiversScreen(
    viewModel: CaregiversViewModel,
    onBack: () -> Unit,
    onLeftBaby: () -> Unit,
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    val texts = mapOf(
        CaregiversMessage.INVITE_SENT to stringResource(R.string.invite_sent),
        CaregiversMessage.INVITE_RESENT to stringResource(R.string.caregivers_resent),
        CaregiversMessage.REMOVED to stringResource(R.string.caregivers_removed),
        CaregiversMessage.ROLE_CHANGED to stringResource(R.string.caregivers_role_changed),
    )
    LaunchedEffect(viewModel) {
        viewModel.events.collect { event ->
            when (event) {
                is CaregiversEvent.Message -> snackbar.showSnackbar(texts.getValue(event.message))
                CaregiversEvent.LeftBaby -> onLeftBaby()
            }
        }
    }
    var roleTarget by remember { mutableStateOf<Membership?>(null) }

    NinaScreen(title = stringResource(R.string.caregivers_title), onBack = onBack, snackbarHost = snackbar) { padding ->
        if (state.loading && state.members.isEmpty()) {
            LoadingBox(Modifier.padding(padding))
            return@NinaScreen
        }
        val visible = CaregiversViewModel.visible(state.members)
        LazyColumn(
            Modifier
                .fillMaxSize()
                .padding(padding),
            contentPadding = PaddingValues(NinaDimens.gutter),
            verticalArrangement = Arrangement.spacedBy(NinaDimens.space3),
        ) {
            state.error?.let { item { ErrorBanner(stringResource(it.messageRes())) } }
            if (!state.isOwner) item { InfoBanner(stringResource(R.string.caregivers_owner_only_note)) }
            items(visible, key = { it.id }) { member ->
                MemberCard(
                    member = member,
                    isMe = state.isMe(member),
                    isOwnerViewing = state.isOwner,
                    busy = state.working,
                    onResend = { viewModel.resend(member) },
                    onCancelInvite = { viewModel.ask(ConfirmAction.CancelInvite(member)) },
                    onChangeRole = { roleTarget = member },
                    onRemove = { viewModel.ask(ConfirmAction.Remove(member)) },
                    onLeave = { viewModel.ask(ConfirmAction.Leave(member)) },
                )
            }
            if (state.isOwner) {
                item { NinaPrimaryButton(stringResource(R.string.caregivers_invite), onClick = viewModel::openInvite) }
            }
        }
    }

    state.invite?.let { form -> InviteDialog(form, viewModel) }
    state.confirm?.let { action -> ConfirmDialog(action, onConfirm = viewModel::confirm, onDismiss = viewModel::dismissConfirm) }
    roleTarget?.let { member ->
        RoleDialog(
            current = member.role.name,
            onPick = {
                viewModel.changeRole(member, it)
                roleTarget = null
            },
            onDismiss = { roleTarget = null },
        )
    }
}

@Composable
private fun MemberCard(
    member: Membership,
    isMe: Boolean,
    isOwnerViewing: Boolean,
    busy: Boolean,
    onResend: () -> Unit,
    onCancelInvite: () -> Unit,
    onChangeRole: () -> Unit,
    onRemove: () -> Unit,
    onLeave: () -> Unit,
) {
    val pending = member.status == MembershipStatus.PENDING
    val name = member.userDisplayName
        ?: member.invitedEmail
        ?: stringResource(R.string.caregivers_invitee_unknown)
    val roleText = stringResource(member.role.labelRes())
    val statusText = stringResource(member.status.labelRes())
    val you = stringResource(R.string.common_you)
    NinaCard(description = stringResource(R.string.caregivers_item_description, if (isMe) "$name $you" else name, roleText, statusText)) {
        Text(
            if (isMe) "$name $you" else name,
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.onSurface,
        )
        // Papel e status por texto (nunca só cor).
        Text("$roleText · $statusText", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (pending) {
            member.invitationExpiresAt?.let {
                Text(
                    stringResource(R.string.caregivers_pending_expires, formatInstantDate(it)),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        Column(Modifier.fillMaxWidth()) {
            if (isOwnerViewing && !isMe && member.role != app.nina.domain.model.Role.OWNER) {
                if (pending) {
                    NinaTextButton(stringResource(R.string.caregivers_resend), onResend, enabled = !busy)
                    NinaDestructiveTextButton(stringResource(R.string.caregivers_cancel_invite), onCancelInvite)
                } else if (member.status == MembershipStatus.ACTIVE) {
                    NinaTextButton(stringResource(R.string.caregivers_change_role), onChangeRole, enabled = !busy)
                    NinaDestructiveTextButton(stringResource(R.string.caregivers_remove), onRemove)
                }
            }
            if (!isOwnerViewing && isMe && member.status == MembershipStatus.ACTIVE) {
                NinaDestructiveTextButton(stringResource(R.string.caregivers_leave), onLeave)
            }
        }
    }
}

@Composable
private fun InviteDialog(form: InviteForm, viewModel: CaregiversViewModel) {
    AlertDialog(
        onDismissRequest = viewModel::closeInvite,
        title = { Text(stringResource(R.string.invite_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(NinaDimens.space3)) {
                NinaTextField(
                    value = form.email,
                    onValueChange = viewModel::onInviteEmail,
                    label = stringResource(R.string.invite_email),
                    keyboardType = KeyboardType.Email,
                    errorText = form.emailIssue?.let { stringResource(it.messageRes) },
                )
                Text(stringResource(R.string.invite_role), style = MaterialTheme.typography.labelMedium)
                Column(Modifier.selectableGroup()) {
                    listOf(InvitableRole.CAREGIVER, InvitableRole.READ_ONLY).forEach { role ->
                        Row(
                            Modifier
                                .fillMaxWidth()
                                .heightIn(min = NinaDimens.minTouch)
                                .selectable(selected = form.role == role, role = Role.RadioButton, onClick = { viewModel.onInviteRole(role) }),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            RadioButton(selected = form.role == role, onClick = null)
                            Column(Modifier.padding(start = NinaDimens.space3)) {
                                Text(stringResource(role.labelRes()), style = MaterialTheme.typography.bodyLarge)
                                Text(
                                    stringResource(role.descriptionRes()),
                                    style = MaterialTheme.typography.labelMedium,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            NinaTextButton(stringResource(R.string.invite_send), onClick = viewModel::sendInvite, enabled = !form.sending)
        },
        dismissButton = { NinaTextButton(stringResource(R.string.common_cancel), onClick = viewModel::closeInvite) },
    )
}

@Composable
private fun ConfirmDialog(action: ConfirmAction, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    val m = action.member
    val name = m.userDisplayName ?: m.invitedEmail ?: stringResource(R.string.caregivers_invitee_unknown)
    val (title, body) = when (action) {
        is ConfirmAction.Remove -> stringResource(R.string.caregivers_remove_title, name) to stringResource(R.string.caregivers_remove_body, name)
        is ConfirmAction.CancelInvite -> stringResource(R.string.caregivers_cancel_invite_title) to stringResource(R.string.caregivers_cancel_invite_body, name)
        is ConfirmAction.Leave -> stringResource(R.string.caregivers_leave_title) to stringResource(R.string.caregivers_leave_body)
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = { Text(body) },
        confirmButton = { NinaDestructiveTextButton(stringResource(R.string.common_confirm), onConfirm) },
        dismissButton = { NinaTextButton(stringResource(R.string.common_cancel), onDismiss) },
    )
}

@Composable
private fun RoleDialog(current: String, onPick: (InvitableRole) -> Unit, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.caregivers_change_role)) },
        text = {
            Column(Modifier.selectableGroup()) {
                listOf(InvitableRole.CAREGIVER, InvitableRole.READ_ONLY).forEach { role ->
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .heightIn(min = NinaDimens.minTouch)
                            .selectable(selected = current == role.name, role = Role.RadioButton, onClick = { onPick(role) }),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        RadioButton(selected = current == role.name, onClick = null)
                        Text(stringResource(role.labelRes()), Modifier.padding(start = NinaDimens.space3), style = MaterialTheme.typography.bodyLarge)
                    }
                }
            }
        },
        confirmButton = {},
        dismissButton = { NinaTextButton(stringResource(R.string.common_cancel), onDismiss) },
    )
}
