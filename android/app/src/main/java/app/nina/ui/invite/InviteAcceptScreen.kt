package app.nina.ui.invite

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.nina.R
import app.nina.ui.components.ErrorBanner
import app.nina.ui.components.NinaCard
import app.nina.ui.components.NinaPrimaryButton
import app.nina.ui.components.NinaScreen
import app.nina.ui.components.NinaSecondaryButton
import app.nina.ui.components.NinaTextField
import app.nina.ui.components.ScrollableColumn
import app.nina.ui.formatInstantDate
import app.nina.ui.labelRes
import app.nina.ui.messageRes

@Composable
fun InviteAcceptScreen(viewModel: InviteAcceptViewModel, onBack: () -> Unit, onDone: () -> Unit) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    val acceptedText = stringResource(R.string.accept_done)
    val declinedText = stringResource(R.string.accept_declined)
    LaunchedEffect(viewModel) {
        viewModel.events.collect { event ->
            snackbar.showSnackbar(if (event == InviteAcceptEvent.Accepted) acceptedText else declinedText)
            onDone()
        }
    }
    NinaScreen(title = stringResource(R.string.accept_title), onBack = onBack, snackbarHost = snackbar) { padding ->
        ScrollableColumn(padding) {
            state.error?.let { ErrorBanner(stringResource(it.messageRes())) }
            NinaTextField(
                value = state.input,
                onValueChange = viewModel::onInput,
                label = stringResource(R.string.accept_token_label),
                supportingText = stringResource(R.string.accept_token_help),
                keyboardType = KeyboardType.Uri,
                errorText = state.inputIssue?.let { stringResource(it.messageRes) },
            )
            val preview = state.preview
            if (preview == null) {
                NinaPrimaryButton(stringResource(R.string.accept_check), onClick = viewModel::inspect, loading = state.busy)
            } else {
                NinaCard {
                    Text(
                        stringResource(R.string.accept_preview, preview.inviterDisplayName, preview.babyLabel),
                        style = MaterialTheme.typography.bodyLarge,
                        color = MaterialTheme.colorScheme.onSurface,
                    )
                    Text(
                        stringResource(R.string.accept_preview_role, stringResource(preview.role.labelRes())),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Text(
                        stringResource(R.string.accept_preview_expires, formatInstantDate(preview.expiresAt)),
                        style = MaterialTheme.typography.labelMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                NinaPrimaryButton(stringResource(R.string.accept_accept), onClick = viewModel::accept, loading = state.busy)
                NinaSecondaryButton(stringResource(R.string.accept_decline), onClick = viewModel::decline, enabled = !state.busy, modifier = Modifier.fillMaxWidth())
            }
        }
    }
}
