package app.nina.ui.tracking

import android.content.Context
import android.content.res.Resources
import androidx.annotation.DrawableRes
import androidx.annotation.StringRes
import app.nina.R
import app.nina.domain.model.BreastSide
import app.nina.domain.model.DiaperType
import app.nina.domain.model.FeedingType
import app.nina.domain.model.MilkType
import app.nina.domain.model.SleepType
import app.nina.domain.tracking.DiaperEvent
import app.nina.domain.tracking.EventField
import app.nina.domain.tracking.FeedingEvent
import app.nina.domain.tracking.IssueCode
import app.nina.domain.tracking.PumpingEvent
import app.nina.domain.tracking.SleepEvent
import app.nina.domain.tracking.SyncState
import app.nina.domain.tracking.TrackedEvent
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/** Visual do tipo de registro: ícone + forma + rótulo, nunca só cor (ux-spec 10.1). */
enum class EventVisual(@DrawableRes val icon: Int) {
    SLEEP(R.drawable.ic_event_sleep),
    BREAST(R.drawable.ic_event_breast),
    BOTTLE(R.drawable.ic_event_bottle),
    DIAPER(R.drawable.ic_event_diaper),
    PUMPING(R.drawable.ic_event_pumping),
}

/** Linha de evento já com os textos localizados e a descrição falada completa para o TalkBack (RF-019-A6). */
data class EventRowUi(
    val id: String,
    val visual: EventVisual,
    val timeLabel: String,
    val title: String,
    val detail: String?,
    val editedBy: String?,
    val syncState: SyncState,
    val syncMarker: String?,
    val contentDescription: String,
)

fun formatClock(instant: Instant, zone: ZoneId, is24Hour: Boolean, locale: Locale): String =
    DateTimeFormatter.ofPattern(if (is24Hour) "HH:mm" else "h:mm a", locale).format(instant.atZone(zone))

/** Cronômetro "H:MM:SS". Só visual; o TalkBack recebe a descrição por minuto, não por segundo. */
fun formatStopwatch(d: Duration): String {
    val total = d.seconds.coerceAtLeast(0)
    return "%d:%02d:%02d".format(Locale.ROOT, total / 3600, (total % 3600) / 60, total % 60)
}

/** "42 min", "1 h 05 min", "3 h". */
fun Resources.durationLabel(d: Duration): String {
    val minutes = d.toMinutes().coerceAtLeast(0)
    val h = (minutes / 60).toInt()
    val m = (minutes % 60).toInt()
    return when {
        h == 0 -> getString(R.string.duration_min, m)
        m == 0 -> getString(R.string.duration_h, h)
        else -> getString(R.string.duration_h_min, h, m)
    }
}

/** "1 hora e 5 minutos" para leitores de tela. */
fun Resources.spokenDuration(d: Duration): String {
    val minutes = d.toMinutes().coerceAtLeast(0)
    val h = (minutes / 60).toInt()
    val m = (minutes % 60).toInt()
    val hours = getQuantityString(R.plurals.spoken_hours, h, h)
    val mins = getQuantityString(R.plurals.spoken_minutes, m, m)
    return when {
        h == 0 && m == 0 -> getString(R.string.duration_less_than_minute)
        h == 0 -> mins
        m == 0 -> hours
        else -> getString(R.string.spoken_h_and_min, hours, mins)
    }
}

@StringRes
fun SleepType.labelRes(): Int = when (this) {
    SleepType.NAP -> R.string.sleep_type_nap
    SleepType.NIGHT -> R.string.sleep_type_night
    SleepType.UNRECOGNIZED -> R.string.sleep_type_unknown
}

@StringRes
fun BreastSide.labelRes(): Int = when (this) {
    BreastSide.LEFT -> R.string.side_left
    BreastSide.RIGHT -> R.string.side_right
    BreastSide.BOTH -> R.string.side_both
    BreastSide.UNRECOGNIZED -> R.string.side_unknown
}

@StringRes
fun DiaperType.labelRes(): Int = when (this) {
    DiaperType.WET -> R.string.diaper_wet
    DiaperType.DIRTY -> R.string.diaper_dirty
    DiaperType.MIXED -> R.string.diaper_mixed
    DiaperType.DRY -> R.string.diaper_dry
    DiaperType.UNSPECIFIED -> R.string.diaper_unspecified
    DiaperType.UNRECOGNIZED -> R.string.diaper_unknown
}

@StringRes
fun MilkType.labelRes(): Int = when (this) {
    MilkType.BREAST_MILK -> R.string.milk_breast
    MilkType.FORMULA -> R.string.milk_formula
    MilkType.MIXED -> R.string.milk_mixed
    MilkType.OTHER -> R.string.milk_other
    MilkType.UNSPECIFIED -> R.string.milk_unspecified
    MilkType.UNRECOGNIZED -> R.string.milk_unknown
}

fun EventVisual.titleRes(): Int = when (this) {
    EventVisual.SLEEP -> R.string.ev_sleep
    EventVisual.BREAST -> R.string.ev_breast
    EventVisual.BOTTLE -> R.string.ev_bottle
    EventVisual.DIAPER -> R.string.ev_diaper
    EventVisual.PUMPING -> R.string.ev_pumping
}

/** Mensagem de erro de campo (ícone + texto na UI). */
@StringRes
fun issueMessageRes(field: EventField, code: IssueCode): Int = when (code) {
    IssueCode.REQUIRED -> when (field) {
        EventField.SIDE -> R.string.issue_side_required
        EventField.DIAPER_TYPE -> R.string.issue_diaper_required
        else -> R.string.issue_required
    }
    IssueCode.END_BEFORE_START -> R.string.issue_end_before_start
    IssueCode.FUTURE_TIME -> R.string.issue_future
    IssueCode.TOO_LONG_SESSION -> R.string.issue_too_long_session
    IssueCode.VOLUME_RANGE -> R.string.issue_volume
    IssueCode.TEXT_TOO_LONG -> R.string.issue_text_too_long
    IssueCode.NOT_APPLICABLE, IssueCode.INVALID -> R.string.issue_invalid
}

/** Monta a linha da timeline. [showAuthor] só quando há mais de um cuidador (RF-019-A5). */
fun describeEvent(context: Context, e: TrackedEvent, zone: ZoneId, is24Hour: Boolean, locale: Locale, showAuthor: Boolean): EventRowUi {
    val r = context.resources
    fun clock(i: Instant) = formatClock(i, zone, is24Hour, locale)
    val visual: EventVisual
    val title: String
    var detail: String? = null
    val spokenWhen: String
    var spokenDuration: String? = null
    var extraSpoken: String? = null
    when (e) {
        is SleepEvent -> {
            visual = EventVisual.SLEEP
            title = r.getString(
                when (e.sleepType) {
                    SleepType.NAP -> R.string.ev_sleep_nap
                    SleepType.NIGHT -> R.string.ev_sleep_night
                    SleepType.UNRECOGNIZED -> R.string.ev_sleep
                },
            )
            val end = e.endAt
            if (end == null) {
                detail = r.getString(R.string.ev_range, clock(e.startAt), r.getString(R.string.ev_in_progress_suffix))
                spokenWhen = r.getString(R.string.a11y_since, clock(e.startAt)) + ", " + r.getString(R.string.ev_in_progress_suffix)
            } else {
                val d = Duration.between(e.startAt, end)
                detail = r.getString(R.string.ev_title_detail, r.getString(R.string.ev_range, clock(e.startAt), clock(end)), r.durationLabel(d))
                spokenWhen = r.getString(R.string.a11y_range, clock(e.startAt), clock(end))
                spokenDuration = r.spokenDuration(d)
            }
        }
        is FeedingEvent -> {
            when (e.feedingType) {
                FeedingType.BREASTFEEDING -> {
                    visual = EventVisual.BREAST
                    title = e.side?.let { r.getString(R.string.ev_title_detail, r.getString(R.string.ev_breast), r.getString(it.labelRes())) }
                        ?: r.getString(R.string.ev_breast)
                }
                FeedingType.BOTTLE -> {
                    visual = EventVisual.BOTTLE
                    title = e.volumeMl?.let { r.getString(R.string.ev_title_detail, r.getString(R.string.ev_bottle), r.getString(R.string.volume_ml, it)) }
                        ?: r.getString(R.string.ev_bottle)
                    detail = e.milkType?.let { r.getString(it.labelRes()) }
                    extraSpoken = detail
                }
                else -> {
                    visual = EventVisual.BOTTLE
                    title = r.getString(R.string.ev_feeding_other)
                }
            }
            val end = e.endAt
            if (end != null) {
                val d = Duration.between(e.startAt, end)
                val range = r.getString(R.string.ev_title_detail, r.getString(R.string.ev_range, clock(e.startAt), clock(end)), r.durationLabel(d))
                detail = listOfNotNull(detail, range).joinToString(" · ")
                spokenWhen = r.getString(R.string.a11y_range, clock(e.startAt), clock(end))
                spokenDuration = r.spokenDuration(d)
            } else {
                spokenWhen = r.getString(R.string.a11y_at, clock(e.startAt))
            }
        }
        is PumpingEvent -> {
            visual = EventVisual.PUMPING
            title = r.getString(R.string.ev_pumping)
            val d = e.duration
            val parts = listOfNotNull(
                r.getString(R.string.ev_title_detail, r.getString(R.string.ev_range, clock(e.startAt), clock(e.endAt)), r.durationLabel(d)),
                e.volumeMl?.let { r.getString(R.string.volume_ml, it) },
                e.side?.let { r.getString(it.labelRes()) },
            )
            detail = parts.joinToString(" · ")
            spokenWhen = r.getString(R.string.a11y_range, clock(e.startAt), clock(e.endAt))
            spokenDuration = r.spokenDuration(d)
        }
        is DiaperEvent -> {
            visual = EventVisual.DIAPER
            title = r.getString(R.string.ev_title_detail, r.getString(R.string.ev_diaper), r.getString(e.diaperType.labelRes()))
            spokenWhen = r.getString(R.string.a11y_at, clock(e.occurredAt))
        }
    }
    val marker = when (e.syncState) {
        SyncState.PENDING -> r.getString(R.string.item_pending_marker)
        SyncState.FAILED -> r.getString(R.string.item_failed_marker)
        SyncState.SYNCED -> null
    }
    val author = e.lastModifiedByName?.takeIf { showAuthor }
    val edited = author?.let { r.getString(R.string.ev_edited_by, it) }
    // "Soneca, das 14:02 às 14:44, 42 minutos, editado por Ana, aguardando envio." (ux-spec 10.1)
    val description = listOfNotNull(title, extraSpoken, spokenWhen, spokenDuration, edited, marker)
        .joinToString(", ")
    val start = e.startInstant
    return EventRowUi(e.id, visual, clock(start), title, detail, edited, e.syncState, marker, description)
}
