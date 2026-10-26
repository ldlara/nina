package app.nina.ui

import app.nina.domain.model.BreastSide
import app.nina.domain.model.DiaperType
import app.nina.domain.model.Role
import app.nina.domain.tracking.BreastfeedingDraft
import app.nina.domain.tracking.DiaperDraft
import app.nina.domain.tracking.QuickDefaults
import app.nina.domain.tracking.SleepDraft
import app.nina.domain.tracking.StubSyncEngine
import app.nina.domain.tracking.SyncIndicator
import app.nina.domain.tracking.SyncRunResult
import app.nina.testutil.FakeBabyRepository
import app.nina.testutil.FakeTrackingRepository
import app.nina.testutil.MainDispatcherRule
import app.nina.testutil.TestClock
import app.nina.testutil.testBaby
import app.nina.ui.timeline.TimelineFilter
import app.nina.ui.timeline.TimelineViewModel
import app.nina.ui.today.TodayMessage
import app.nina.ui.today.TodayViewModel
import app.nina.ui.tracking.EventFormViewModel
import app.nina.ui.tracking.FormKind
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import java.time.Duration
import java.time.Instant

class TrackingViewModelsTest {
    @get:Rule val main = MainDispatcherRule()

    private val clock = TestClock(Instant.parse("2026-10-08T17:00:00Z")) // 14:00 em São Paulo
    private val babies = FakeBabyRepository().also { it.babies.value = listOf(testBaby(id = "b-1")) }
    private val repo = FakeTrackingRepository()
    private val engine = StubSyncEngine()

    private fun today() = TodayViewModel("b-1", babies, repo, flowOf(SyncIndicator.Synced), engine, clock)
    private fun timeline() = TimelineViewModel("b-1", babies, repo, flowOf(SyncIndicator.Synced), engine, clock, pageSize = 3)
    private fun form(id: String? = null, kind: FormKind? = FormKind.BREASTFEEDING) =
        EventFormViewModel("b-1", id, kind, babies, repo, clock)

    @Test fun `Dormiu cria sono de timer em andamento e Acordou fecha com sheet`() {
        val vm = today()
        vm.startSleep()
        assertNotNull(vm.state.value.openSleep)
        assertEquals(TodayMessage.SleepStarted, vm.state.value.message)
        clock.advance(Duration.ofMinutes(42))
        vm.stopSleep()
        assertNull(vm.state.value.openSleep)
        assertEquals(Duration.ofMinutes(42), vm.state.value.justStopped!!.duration)
        vm.undoStop()
        assertNotNull("desfazer reabre o timer", vm.state.value.openSleep)
    }

    @Test fun `Foi antes move o inicio do timer e fim retroativo nunca fica antes do inicio`() {
        val vm = today()
        vm.startSleep(minutesAgo = 10)
        val start = vm.state.value.openSleep!!.startAt
        assertEquals(clock.now.minus(Duration.ofMinutes(10)), start)
        vm.moveSleepStartEarlier(15)
        assertEquals(start.minus(Duration.ofMinutes(15)), vm.state.value.openSleep!!.startAt)
        vm.stopSleep(minutesAgo = 600)
        assertTrue(vm.state.value.justStopped!!.endAt!! > vm.state.value.justStopped!!.startAt)
    }

    @Test fun `segundo Dormiu leva ao sono existente`() {
        val vm = today()
        repo.failNextCreate = app.nina.domain.tracking.SaveResult.Rejected(app.nina.domain.tracking.Rejection.OpenSleepExists("x"))
        vm.startSleep()
        assertEquals(TodayMessage.OpenSleepExists, vm.state.value.message)
    }

    @Test fun `fralda em um toque salva agora e pode ser desfeita`() {
        val vm = today()
        vm.quickDiaper(DiaperType.WET)
        val msg = vm.state.value.message as TodayMessage.DiaperSaved
        assertEquals(DiaperDraft(clock.now, DiaperType.WET), repo.created.single())
        vm.undoCreated(msg.eventId)
        assertEquals(listOf(msg.eventId), repo.deleted)
        assertEquals(1, vm.state.value.summary.diaperCount + 1)
    }

    @Test fun `leitura apenas nao pode registrar e resumo conta o dia`() {
        babies.babies.value = listOf(testBaby(id = "b-1", role = Role.READ_ONLY))
        assertFalse(today().state.value.canRecord)
        babies.babies.value = listOf(testBaby(id = "b-1"))
        val vm = today()
        vm.quickDiaper(DiaperType.DIRTY)
        assertEquals(1, vm.state.value.summary.diaperCount)
        assertFalse(vm.state.value.noRecordsToday)
    }

    @Test fun `tentar agora com motor stub informa que ainda nao esta disponivel`() {
        val vm = today()
        vm.syncNow()
        assertEquals(TodayMessage.SyncNotAvailable, vm.state.value.message)
        assertEquals(SyncRunResult.NotAvailable, kotlinx.coroutines.runBlocking { engine.syncNow() })
    }

    @Test fun `pergunta de sono longo aparece apos 6 horas e pode ser dispensada`() {
        val vm = today()
        vm.startSleep()
        clock.advance(Duration.ofHours(7))
        vm.tick()
        assertTrue(vm.state.value.longSleepPrompt)
        vm.dismissLongSleepPrompt()
        assertFalse(vm.state.value.longSleepPrompt)
    }

    @Test fun `timeline agrupa por dia no fuso do bebe, filtra e mostra hoje vazio`() = runTest {
        val vm = timeline()
        assertTrue(vm.state.value.noEventsAtAll)
        assertEquals(1, vm.state.value.sections.size) // "Hoje" sempre presente
        assertTrue(vm.state.value.sections.single().items.isEmpty())

        // 23:30 local de ontem (02:30Z de hoje) pertence a ontem; 14:00 local pertence a hoje
        repo.create("b-1", DiaperDraft(Instant.parse("2026-10-08T02:30:00Z"), DiaperType.WET))
        repo.create("b-1", DiaperDraft(Instant.parse("2026-10-08T17:00:00Z"), DiaperType.DIRTY))
        repo.create("b-1", BreastfeedingDraft(BreastSide.LEFT, Instant.parse("2026-10-08T16:00:00Z"), Instant.parse("2026-10-08T16:10:00Z")))
        val days = vm.state.value.sections
        assertEquals(2, days.size)
        assertEquals(2, days[0].items.size)
        assertEquals(1, days[1].items.size)
        assertEquals(1, days[0].summary!!.feedingCount)

        vm.setFilter(TimelineFilter.DIAPER)
        assertEquals(1, vm.state.value.sections.first().items.size)
        vm.setFilter(TimelineFilter.PUMPING)
        assertTrue(vm.state.value.sections.first().items.isEmpty())
    }

    @Test fun `timeline pagina com janela local e esconde totais do dia cortado`() {
        val vm = timeline()
        kotlinx.coroutines.runBlocking {
            repeat(5) { i -> repo.create("b-1", DiaperDraft(clock.now.minusSeconds(60L * i), DiaperType.WET)) }
        }
        assertTrue(vm.state.value.hasMore)
        assertNull("dia possivelmente incompleto fica sem totais", vm.state.value.sections.last().summary)
        vm.loadMore()
        assertFalse(vm.state.value.hasMore)
        assertNotNull(vm.state.value.sections.last().summary)
    }

    @Test fun `formulario de mamada usa ultimo lado e duracao, exige lado e grava end_at`() {
        repo.defaults = QuickDefaults(lastBreastSide = BreastSide.RIGHT, lastBreastMinutes = 12)
        val vm = form()
        assertEquals(BreastSide.RIGHT, vm.state.value.side)
        assertEquals(12, vm.state.value.durationMinutes)
        assertEquals(clock.now, vm.state.value.end)
        vm.save()
        assertTrue(vm.state.value.done)
        val d = repo.created.single() as BreastfeedingDraft
        assertEquals(clock.now, d.endAt)
        assertEquals(clock.now.minus(Duration.ofMinutes(12)), d.startAt)
    }

    @Test fun `mamada sem lado nao salva e mostra o erro no campo`() {
        val vm = form()
        vm.save()
        assertFalse(vm.state.value.done)
        assertNotNull(vm.state.value.issue(app.nina.domain.tracking.EventField.SIDE))
        assertTrue(repo.created.isEmpty())
    }

    @Test fun `mamadeira parte do ultimo volume e atalhos ajustam o horario`() {
        repo.defaults = QuickDefaults(lastBottleVolumeMl = 120)
        val vm = form(kind = FormKind.BOTTLE)
        assertEquals(120, vm.state.value.volumeMl)
        vm.adjustVolume(10)
        vm.setMinutesAgo(15)
        assertEquals(130, vm.state.value.volumeMl)
        assertEquals(clock.now.minus(Duration.ofMinutes(15)), vm.state.value.start)
    }

    @Test fun `editar carrega o evento e excluir remove`() {
        val sleep = (kotlinx.coroutines.runBlocking { repo.create("b-1", SleepDraft(app.nina.domain.model.SleepType.NAP, clock.now.minusSeconds(3600), clock.now.minusSeconds(1800), source = app.nina.domain.model.SleepSource.MANUAL)) } as app.nina.domain.tracking.SaveResult.Saved).value
        val vm = form(id = sleep.id, kind = null)
        assertTrue(vm.state.value.isEdit)
        assertEquals(FormKind.SLEEP, vm.state.value.kind)
        vm.delete()
        assertTrue(vm.state.value.done)
        assertEquals(listOf(sleep.id), repo.deleted)
    }
}
