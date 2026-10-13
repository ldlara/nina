package app.nina.ui

import androidx.annotation.StringRes
import app.nina.R
import app.nina.domain.model.AppError
import app.nina.domain.model.FieldError

/**
 * Mensagens localizadas por `code` (RF-050-A5, AD-06). `title`/`detail` da API nunca são exibidos.
 */
@StringRes
fun AppError.messageRes(): Int = when (this) {
    AppError.Network -> R.string.error_network
    AppError.SocialUnavailable -> R.string.auth_social_unavailable
    is AppError.Unexpected -> R.string.error_generic
    is AppError.Api -> when (code) {
        "INVALID_CREDENTIALS" -> R.string.error_invalid_credentials
        "TOKEN_EXPIRED", "SESSION_REVOKED", "REFRESH_TOKEN_REUSED" -> R.string.error_session_expired
        "VALIDATION_FAILED" -> R.string.error_validation
        "FORBIDDEN_ROLE" -> R.string.error_forbidden_role
        "ACCESS_REVOKED" -> R.string.error_access_revoked
        "CONSENT_REQUIRED" -> R.string.error_consent_required
        "NOT_FOUND" -> R.string.error_not_found
        "IDENTITY_LINK_REQUIRED" -> R.string.error_identity_link_required
        "ALREADY_MEMBER" -> R.string.error_already_member
        "RATE_LIMITED" -> R.string.error_rate_limited
        "VERSION_CONFLICT" -> R.string.error_version_conflict
        "CLIENT_UPGRADE_REQUIRED" -> R.string.error_upgrade_required
        "OWNER_REQUIRED" -> R.string.error_owner_required
        "IDEMPOTENCY_KEY_REUSE" -> R.string.error_idempotency
        "INTERNAL", "UNAVAILABLE" -> R.string.error_server
        else -> if (httpStatus >= 500) R.string.error_server else R.string.error_generic
    }
}

/** Mensagem de um erro de campo (`errors[].code`). */
@StringRes
fun FieldError.messageRes(): Int = when (code) {
    "REQUIRED" -> R.string.field_required
    "FUTURE_DATE" -> R.string.field_future_date
    "PASSWORD_POLICY" -> R.string.field_password_policy
    else -> R.string.field_invalid
}

/** Primeiro erro de campo cujo caminho termina em [name] (ex.: `data.birth_date` ou `birth_date`). */
fun AppError.fieldError(name: String): FieldError? =
    (this as? AppError.Api)?.fieldErrors?.firstOrNull { it.field == name || it.field.endsWith(".$name") }
