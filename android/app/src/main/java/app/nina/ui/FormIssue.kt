package app.nina.ui

import androidx.annotation.StringRes
import app.nina.R

/** Problemas de validação de formulário, independentes de idioma (a UI resolve o texto). */
enum class FormIssue(@StringRes val messageRes: Int) {
    REQUIRED(R.string.field_required),
    INVALID_EMAIL(R.string.field_email_invalid),
    PASSWORD_POLICY(R.string.field_password_policy),
    FUTURE_DATE(R.string.field_future_date),
    NAME_TOO_LONG(R.string.field_name_too_long),
    INVALID(R.string.field_invalid),
    ;

    companion object {
        /** Converte `errors[].code` do servidor. */
        fun fromServer(code: String): FormIssue = when (code) {
            "REQUIRED" -> REQUIRED
            "FUTURE_DATE" -> FUTURE_DATE
            "PASSWORD_POLICY" -> PASSWORD_POLICY
            else -> INVALID
        }
    }
}

private val EMAIL_REGEX = Regex("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$")

fun isPlausibleEmail(value: String): Boolean = EMAIL_REGEX.matches(value.trim())

const val BABY_NAME_MAX_LENGTH = 60
