package app.nina.ui

import android.content.Context
import app.nina.R
import app.nina.domain.model.MembershipStatus
import app.nina.domain.model.Role
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale

fun formatDate(date: LocalDate, locale: Locale = Locale.getDefault()): String =
    date.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).withLocale(locale))

fun formatInstantDate(instant: Instant, zone: ZoneId = ZoneId.systemDefault(), locale: Locale = Locale.getDefault()): String =
    formatDate(instant.atZone(zone).toLocalDate(), locale)

/** "271 dias (38 semanas)" ou "5 dias". Semanas completas, só como apoio: a fonte canônica é a contagem em dias. */
fun formatAgeDays(context: Context, days: Int): String {
    val res = context.resources
    val daysText = res.getQuantityString(R.plurals.age_days, days, days)
    if (days < 7) return daysText
    val weeks = days / 7
    return context.getString(R.string.age_days_and_weeks, daysText, res.getQuantityString(R.plurals.age_weeks, weeks, weeks))
}

fun Role.labelRes(): Int = when (this) {
    Role.OWNER -> R.string.role_owner
    Role.CAREGIVER -> R.string.role_caregiver
    Role.READ_ONLY -> R.string.role_read_only
    Role.UNRECOGNIZED -> R.string.role_unknown
}

fun MembershipStatus.labelRes(): Int = when (this) {
    MembershipStatus.ACTIVE -> R.string.status_active
    MembershipStatus.PENDING -> R.string.status_pending
    MembershipStatus.REVOKED -> R.string.status_revoked
    MembershipStatus.DECLINED -> R.string.status_declined
    MembershipStatus.EXPIRED -> R.string.status_expired
    MembershipStatus.UNRECOGNIZED -> R.string.status_unknown
}

fun app.nina.domain.model.InvitableRole.labelRes(): Int = when (this) {
    app.nina.domain.model.InvitableRole.CAREGIVER -> R.string.role_caregiver
    app.nina.domain.model.InvitableRole.READ_ONLY -> R.string.role_read_only
    app.nina.domain.model.InvitableRole.UNRECOGNIZED -> R.string.role_unknown
}

fun app.nina.domain.model.InvitableRole.descriptionRes(): Int = when (this) {
    app.nina.domain.model.InvitableRole.CAREGIVER -> R.string.role_caregiver_description
    app.nina.domain.model.InvitableRole.READ_ONLY -> R.string.role_read_only_description
    app.nina.domain.model.InvitableRole.UNRECOGNIZED -> R.string.role_unknown
}
