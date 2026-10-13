package app.nina.tracking

import app.nina.domain.model.DiaperType
import app.nina.domain.model.SleepSource
import app.nina.domain.model.SleepType
import app.nina.domain.model.WakeSource
import app.nina.domain.tracking.DaySummary
import app.nina.domain.tracking.DayWindow
import app.nina.domain.tracking.DiaperEvent
import app.nina.domain.tracking.SleepEvent
import app.nina.domain.tracking.SyncIndicator
import app.nina.domain.tracking.SyncState
import app.nina.domain.tracking.SyncStatus
import app.nina.domain.tracking.WakeEvent
import app.nina.domain.tracking.deriveSyncIndicator
import app.nina.domain.tracking.inferSleepType
import app.nina.domain.tracking.nightAwakenings
import app.nina.domain.tracking.resolveLocal
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId

class DomainTrackingTest {
    private val ny = ZoneId.of("America/New_York")

    private fun sleep(id: String, type: SleepType, start: Instant, end: Instant?) =
        SleepEvent(id, "b", 1, "UTC", SyncState.SYNCED, start, null, type, start, end, null, null, SleepSource.MANUAL)

    @Test fun `dias com horario de verao duram 23 h e 25 h`() {
        assertEquals(Duration.ofHours(23), DayWindow.of(LocalDate.of(2026, 3, 8), ny).length)
        assertEquals(Duration.ofHours(25), DayWindow.of(LocalDate.of(2026, 11, 1), ny).length)
        assertEquals(Duration.ofHours(24), DayWindow.of(LocalDate.of(2026, 10, 8), ny).length)
    }

    @Test fun `hora inexistente e repetida resolvem sem excecao`() {
        // 02:30 de 8/mar/2026 não existe em NY (salto 02:00 -> 03:00): avança para 03:30 EDT
        assertEquals(Instant.parse("2026-03-08T07:30:00Z"), resolveLocal(LocalDate.of(2026, 3, 8), LocalTime.of(2, 30), ny))
        // 01:30 de 1/nov/2026 ocorre duas vezes: usa a primeira (EDT)
        assertEquals(Instant.parse("2026-11-01T05:30:00Z"), resolveLocal(LocalDate.of(2026, 11, 1), LocalTime.of(1, 30), ny))
    }

    @Test fun `resumo do dia recorta sono que cruza a meia-noite e ignora sessao aberta`() {
        val w = DayWindow.of(LocalDate.of(2026, 10, 8), ZoneId.of("UTC"))
        val night = sleep("n", SleepType.NIGHT, Instant.parse("2026-10-07T21:00:00Z"), Instant.parse("2026-10-08T06:00:00Z"))
        val nap = sleep("p", SleepType.NAP, Instant.parse("2026-10-08T12:00:00Z"), Instant.parse("2026-10-08T13:00:00Z"))
        val open = sleep("o", SleepType.NAP, Instant.parse("2026-10-08T15:00:00Z"), null)
        val diaper = DiaperEvent("d", "b", 1, "UTC", SyncState.SYNCED, w.start, null, Instant.parse("2026-10-08T09:00:00Z"), DiaperType.WET, null)
        val s = DaySummary.of(listOf(night, nap, open, diaper), w)
        assertEquals(Duration.ofHours(7), s.sleepTotal)
        assertEquals(1, s.napCount)
        assertEquals(1, s.diaperCount)
    }

    @Test fun `tipo de sono e inferido pelo horario local do bebe`() {
        val z = ZoneId.of("America/Sao_Paulo")
        assertEquals(SleepType.NIGHT, inferSleepType(Instant.parse("2026-10-08T22:30:00Z"), z)) // 19:30 local
        assertEquals(SleepType.NAP, inferSleepType(Instant.parse("2026-10-08T17:00:00Z"), z)) // 14:00 local
    }

    @Test fun `night_awakenings segue a convencao de nulos do contrato`() {
        val start = Instant.parse("2026-10-07T22:00:00Z")
        val night = sleep("n", SleepType.NIGHT, start, start.plus(Duration.ofHours(8)))
        val wake = WakeEvent("w", "b", 1, SyncState.SYNCED, "n", start.plusSeconds(3600), start.plusSeconds(4000), WakeSource.MANUAL)
        assertEquals(1, nightAwakenings(night, listOf(wake), 240))
        assertEquals(0, nightAwakenings(night, emptyList(), 240))
        assertNull("sessão curta = dados insuficientes", nightAwakenings(sleep("s", SleepType.NIGHT, start, start.plus(Duration.ofHours(2))), emptyList(), 240))
        assertNull("soneca não se aplica", nightAwakenings(sleep("s", SleepType.NAP, start, start.plus(Duration.ofHours(8))), emptyList(), 240))
        assertNull("em aberto", nightAwakenings(sleep("s", SleepType.NIGHT, start, null), emptyList(), 240))
    }

    @Test fun `indicador de sync escolhe o estado certo`() {
        val since = Instant.EPOCH
        assertEquals(SyncIndicator.Offline(3), deriveSyncIndicator(false, SyncStatus.Idle, 3, 0))
        assertEquals(SyncIndicator.Error(since, 2), deriveSyncIndicator(true, SyncStatus.Error(since, null), 2, 0))
        assertEquals(SyncIndicator.Syncing, deriveSyncIndicator(true, SyncStatus.Syncing, 2, 0))
        assertEquals(SyncIndicator.Rejected(1), deriveSyncIndicator(true, SyncStatus.Idle, 0, 1))
        assertEquals(SyncIndicator.Pending(2), deriveSyncIndicator(true, SyncStatus.Idle, 2, 0))
        assertEquals(SyncIndicator.Synced, deriveSyncIndicator(true, SyncStatus.Idle, 0, 0))
    }
}
