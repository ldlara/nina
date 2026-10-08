package app.nina.ui.auth

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.nina.R
import app.nina.domain.model.IdentityProvider
import app.nina.ui.components.ErrorBanner
import app.nina.ui.components.InfoBanner
import app.nina.ui.components.NinaCheckboxRow
import app.nina.ui.components.NinaPrimaryButton
import app.nina.ui.components.NinaScreen
import app.nina.ui.components.NinaSecondaryButton
import app.nina.ui.components.NinaTextButton
import app.nina.ui.components.NinaTextField
import app.nina.ui.components.ScrollableColumn
import app.nina.ui.components.SectionTitle
import app.nina.ui.messageRes
import app.nina.ui.theme.NinaDimens

@Composable
fun AuthScreen(
    viewModel: AuthViewModel,
    sessionExpired: Boolean,
    onBack: (() -> Unit)?,
    onVerificationRequired: (email: String, resendAfterSeconds: Int?) -> Unit,
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    LaunchedEffect(viewModel) {
        viewModel.events.collect { event ->
            when (event) {
                is AuthEvent.VerificationRequired -> onVerificationRequired(event.email, event.resendAfterSeconds)
            }
        }
    }
    AuthContent(
        state = state,
        sessionExpired = sessionExpired,
        onBack = onBack,
        onMode = viewModel::setMode,
        onEmail = viewModel::onEmail,
        onPassword = viewModel::onPassword,
        onDisplayName = viewModel::onDisplayName,
        onToggleShowPassword = viewModel::toggleShowPassword,
        onAcceptTerms = viewModel::onAcceptTerms,
        onAcceptAnalytics = viewModel::onAcceptAnalytics,
        onAcceptMarketing = viewModel::onAcceptMarketing,
        onSubmit = viewModel::submit,
        onSocial = viewModel::onSocial,
        onRetryLegal = viewModel::loadLegal,
    )
}

@Composable
fun AuthContent(
    state: AuthUiState,
    sessionExpired: Boolean,
    onBack: (() -> Unit)?,
    onMode: (AuthMode) -> Unit,
    onEmail: (String) -> Unit,
    onPassword: (String) -> Unit,
    onDisplayName: (String) -> Unit,
    onToggleShowPassword: () -> Unit,
    onAcceptTerms: (Boolean) -> Unit,
    onAcceptAnalytics: (Boolean) -> Unit,
    onAcceptMarketing: (Boolean) -> Unit,
    onSubmit: () -> Unit,
    onSocial: (IdentityProvider) -> Unit,
    onRetryLegal: () -> Unit,
) {
    val register = state.mode == AuthMode.REGISTER
    NinaScreen(
        title = stringResource(if (register) R.string.auth_title_register else R.string.auth_title_login),
        onBack = onBack,
    ) { padding ->
        ScrollableColumn(padding) {
            if (sessionExpired) InfoBanner(stringResource(R.string.error_session_expired))
            state.error?.let { ErrorBanner(stringResource(it.messageRes())) }

            if (register) {
                NinaTextField(
                    value = state.displayName,
                    onValueChange = onDisplayName,
                    label = stringResource(R.string.auth_display_name),
                )
            }
            NinaTextField(
                value = state.email,
                onValueChange = onEmail,
                label = stringResource(R.string.auth_email),
                keyboardType = KeyboardType.Email,
                errorText = state.emailIssue?.let { stringResource(it.messageRes) },
            )
            NinaTextField(
                value = state.password,
                onValueChange = onPassword,
                label = stringResource(R.string.auth_password),
                keyboardType = KeyboardType.Password,
                visualTransformation = if (state.showPassword) VisualTransformation.None else PasswordVisualTransformation(),
                errorText = state.passwordIssue?.let { stringResource(it.messageRes) },
                supportingText = if (register) stringResource(R.string.auth_password_help) else null,
                trailingIcon = {
                    TextButton(onClick = onToggleShowPassword) {
                        Text(stringResource(if (state.showPassword) R.string.auth_hide_password else R.string.auth_show_password))
                    }
                },
            )

            if (register) ConsentsSection(state, onAcceptTerms, onAcceptAnalytics, onAcceptMarketing, onRetryLegal)

            NinaPrimaryButton(
                text = stringResource(if (register) R.string.auth_submit_register else R.string.auth_submit_login),
                onClick = onSubmit,
                loading = state.submitting,
                enabled = !register || state.legal is LegalState.Loaded,
            )

            Row(
                Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(NinaDimens.space3),
            ) {
                HorizontalDivider(Modifier.weight(1f))
                Text(stringResource(R.string.common_or), color = MaterialTheme.colorScheme.onSurfaceVariant)
                HorizontalDivider(Modifier.weight(1f))
            }

            // Google e Apple chamam provedores atrás de SocialAuthProvider (stub neste build).
            NinaSecondaryButton(stringResource(R.string.auth_google), onClick = { onSocial(IdentityProvider.GOOGLE) }, enabled = !state.submitting)
            NinaSecondaryButton(stringResource(R.string.auth_apple), onClick = { onSocial(IdentityProvider.APPLE) }, enabled = !state.submitting)

            NinaTextButton(
                text = stringResource(if (register) R.string.auth_switch_to_login else R.string.auth_switch_to_register),
                onClick = { onMode(if (register) AuthMode.LOGIN else AuthMode.REGISTER) },
                modifier = Modifier.fillMaxWidth(),
            )
        }
    }
}

@Composable
private fun ConsentsSection(
    state: AuthUiState,
    onAcceptTerms: (Boolean) -> Unit,
    onAcceptAnalytics: (Boolean) -> Unit,
    onAcceptMarketing: (Boolean) -> Unit,
    onRetryLegal: () -> Unit,
) {
    when (state.legal) {
        LegalState.Loading -> InfoBanner(stringResource(R.string.auth_legal_loading))
        LegalState.Failed -> {
            ErrorBanner(stringResource(R.string.auth_legal_unavailable))
            NinaSecondaryButton(stringResource(R.string.common_retry), onClick = onRetryLegal)
        }
        is LegalState.Loaded -> Unit
    }
    // Sem pré-marcação (RF-003): termos obrigatórios e opcionais separados e desmarcados por padrão.
    NinaCheckboxRow(
        checked = state.acceptTerms,
        onCheckedChange = onAcceptTerms,
        text = stringResource(R.string.auth_accept_terms, state.termsVersion ?: "…"),
        errorText = if (state.termsMissing) stringResource(R.string.auth_accept_terms_required) else null,
    )
    SectionTitle(stringResource(R.string.auth_optional_consents))
    NinaCheckboxRow(state.acceptAnalytics, onAcceptAnalytics, stringResource(R.string.auth_consent_analytics))
    NinaCheckboxRow(state.acceptMarketing, onAcceptMarketing, stringResource(R.string.auth_consent_marketing))
}
