package app.nina.domain.tracking

import app.nina.domain.model.FeedingType
import app.nina.domain.model.SleepType
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime

/** Janela [start, end) de um dia civil no fuso do bebê. Em dias de horário de verão dura 23 h ou 25 h (RB-014). */
data class DayWindow(val date: LocalDate, val zone: ZoneId, val start: Instant, val end: Instant) {
    val length: Duration get() = Duration.between(start, end)

    companion object {
        fun of(date: LocalDate, zone: ZoneId): DayWindow = DayWindow(
            date = date,
            zone = zone,
            start = date.atStartOfDay(zone).toInstant(),
            end = date.plusDays(1).atStartOfDay(zone).toInstant(),
        )

        fun containing(instant: Instant, zone: ZoneId): DayWindow = of(instant.atZone(zone).toLocalDate(), zone)
    }
}

/** Dia civil de um evento no fuso do bebê (atribuição pelo início; D-13 segue em aberto no produto). */
fun Instant.localDate(zone: ZoneId): LocalDate = atZone(zone).toLocalDate()

/**
 * Resolve data + hora de relógio num instante. Em horário de verão: hora inexistente (salto) avança pela duração do
 * salto; hora repetida usa a primeira ocorrência (offset de verão). Nunca lança exceção.
 */
fun resolveLocal(date: LocalDate, time: LocalTime, zone: ZoneId): Instant =
    ZonedDateTime.of(LocalDateTime.of(date, time), zone).toInstant()

/** Sugere o tipo de sono pelo horário local (ux-spec 4.2: "tipo inferido por horário/rotina, editável"). */
fun inferSleepType(start: Instant, zone: ZoneId): SleepType {
    val hour = start.atZone(zone).toLocalTime()
    // Noturno: das 19h às 6h. A rotina do bebê (RF-013) poderá substituir esta heurística.
    return if (hour >= LocalTime.of(19, 0) || hour < LocalTime.of(6, 0)) SleepType.NIGHT else SleepType.NAP
}

/** Totais do dia (RF-010 e ux-spec 5.1). Valores derivados, recalculados dos eventos, nunca persistidos (RF-010-A7). */
data class DaySummary(
    /** Soma de sessões fechadas, recortadas pela janela do dia. Sessão em andamento fica de fora (RF-010-A6). */
    val sleepTotal: Duration,
    val napCount: Int,
    val feedingCount: Int,
    val diaperCount: Int,
    val pumpingCount: Int,
) {
    companion object {
        val EMPTY = DaySummary(Duration.ZERO, 0, 0, 0, 0)

        fun of(events: List<TrackedEvent>, window: DayWindow): DaySummary {
            var sleep = Duration.ZERO
            var naps = 0
            var feedings = 0
            var diapers = 0
            var pumpings = 0
            for (e in events) {
                when (e) {
                    is SleepEvent -> {
                        val end = e.endAt ?: continue
                        val from = maxOf(e.startAt, window.start)
                        val to = minOf(end, window.end)
                        if (to.isAfter(from)) sleep = sleep.plus(Duration.between(from, to))
                        // Contagem de sonecas pelo dia do início.
                        if (e.sleepType == SleepType.NAP && e.startAt >= window.start && e.startAt < window.end) naps++
                    }
                    is FeedingEvent -> if (inWindow(e.startAt, window)) feedings++
                    is DiaperEvent -> if (inWindow(e.occurredAt, window)) diapers++
                    is PumpingEvent -> if (inWindow(e.startAt, window)) pumpings++
                }
            }
            return DaySummary(sleep, naps, feedings, diapers, pumpings)
        }

        private fun inWindow(at: Instant, w: DayWindow) = at >= w.start && at < w.end
    }
}

/**
 * `night_awakenings` derivado dos `WakeEvent` (ADR-0009): `null` = não se aplica (soneca), sessão aberta ou
 * acompanhamento insuficiente; `0` = acompanhamento suficiente e nenhum despertar; `N` = quantidade.
 */
fun nightAwakenings(sleep: SleepEvent, wakes: List<WakeEvent>, minSessionMinutes: Int): Int? {
    if (sleep.sleepType != SleepType.NIGHT) return null
    val duration = sleep.duration ?: return null
    if (duration.toMinutes() < minSessionMinutes) return null
    return wakes.count { it.sleepSessionId == sleep.id }
}

/** Último momento de mamada (peito ou mamadeira) entre os eventos, para "última mamada há X". */
fun lastFeedingStart(events: List<TrackedEvent>): Instant? =
    events.filterIsInstance<FeedingEvent>()
        .filter { it.feedingType == FeedingType.BREASTFEEDING || it.feedingType == FeedingType.BOTTLE }
        .maxOfOrNull { it.startAt }
