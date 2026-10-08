package app.nina.ui.auth

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import app.nina.R
import app.nina.domain.model.AppError
import app.nina.ui.components.ErrorBanner
import app.nina.ui.components.InfoBanner
import app.nina.ui.components.NinaPrimaryButton
import app.nina.ui.components.NinaScreen
import app.nina.ui.components.NinaSecondaryButton
import app.nina.ui.components.NinaTextField
import app.nina.ui.components.ScrollableColumn
import app.nina.ui.messageRes

@Composable
fun VerifyEmailScreen(viewModel: VerifyEmailViewModel, onBack: () -> Unit) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    NinaScreen(title = stringResource(R.string.verify_title), onBack = onBack) { padding ->
        ScrollableColumn(padding) {
            Text(
                stringResource(R.string.verify_body, state.email),
                style = MaterialTheme.typography.bodyLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            state.error?.let { error ->
                // Código errado/expirado volta como 401 genérico (anti-enumeração).
                val res = if (error is AppError.Api && (error.httpStatus == 401 || error.httpStatus == 400 && error.code != "VALIDATION_FAILED")) {
                    R.string.error_code_invalid
                } else {
                    error.messageRes()
                }
                ErrorBanner(stringResource(res))
            }
            if (state.resent) InfoBanner(stringResource(R.string.verify_resent))
            NinaTextField(
                value = state.code,
                onValueChange = viewModel::onCode,
                label = stringResource(R.string.verify_code),
                keyboardType = KeyboardType.Ascii,
            )
            NinaPrimaryButton(
                text = stringResource(R.string.verify_submit),
                onClick = viewModel::submit,
                loading = state.submitting,
            )
            NinaSecondaryButton(
                text = if (state.resendInSeconds > 0) stringResource(R.string.verify_resend_in, state.resendInSeconds)
                else stringResource(R.string.verify_resend),
                onClick = viewModel::resend,
                enabled = state.resendInSeconds == 0,
                modifier = Modifier.fillMaxWidth(),
            )
        }
    }
}
