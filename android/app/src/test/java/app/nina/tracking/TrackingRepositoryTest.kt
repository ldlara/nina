package app.nina.tracking

import androidx.test.core.app.ApplicationProvider
import app.nina.domain.model.BreastSide
import app.nina.domain.model.DiaperType
import app.nina.domain.model.MilkType
import app.nina.domain.model.MutationOp
import app.nina.domain.model.Outcome
import app.nina.domain.model.Role
import app.nina.domain.model.SleepSource
import app.nina.domain.model.SleepType
import app.nina.domain.tracking.BottleDraft
import app.nina.domain.tracking.BreastfeedingDraft
import app.nina.domain.tracking.DiaperDraft
import app.nina.domain.tracking.EventField
import app.nina.domain.tracking.FeedingEvent
import app.nina.domain.tracking.IssueCode
import app.nina.domain.tracking.Rejection
import app.nina.domain.tracking.SaveResult
import app.nina.domain.tracking.SleepDraft
import app.nina.domain.tracking.SleepEvent
import app.nina.domain.tracking.SyncState
import app.nina.domain.tracking.WakeDraft
import app.nina.testutil.BABY_ID
import app.nina.testutil.TrackingEnv
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Duration
import java.time.LocalDate
import java.time.ZoneId

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], application = android.app.Application::class)
class TrackingRepositoryTest {
    private lateinit var env: TrackingEnv
    private val now get() = env.clock.now

    @Before fun setUp() {
        env = TrackingEnv(ApplicationProvider.getApplicationContext())
        runBlocking { env.seedBaby() }
    }

    @After fun tearDown() = env.close()

    private suspend fun <T> saved(r: SaveResult<T>): T = (r as SaveResult.Saved).value
    private fun timer() = SleepDraft(SleepType.NAP, now.minusSeconds(600), null, source = SleepSource.TIMER)

    @Test fun `escrita grava local e fila na mesma operacao e aciona o sync`() = runBlocking {
        val e = saved(env.repo.create(BABY_ID, DiaperDraft(now, DiaperType.WET, "ok")))
        assertEquals(SyncState.PENDING, e.syncState)
        assertEquals(0, e.version)
        assertEquals("America/Sao_Paulo", e.tz)
        val m = env.queue.snapshot().single()
        assertEquals(MutationOp.CREATE, m.op)
        assertEquals(e.id, m.entityId)
        assertEquals("WET", m.data!!["diaper_type"]!!.toString().trim('"'))
        assertEquals(1, env.writes.size)
    }

    @Test fun `timer inicia, para e nao permite segundo sono aberto`() = runBlocking {
        val s = saved(env.repo.create(BABY_ID, timer())) as SleepEvent
        assertTrue(s.isOpen)
        val second = env.repo.create(BABY_ID, timer())
        assertEquals(Rejection.OpenSleepExists(s.id), (second as SaveResult.Rejected).reason)
        val stopped = saved(env.repo.update(s.id, SleepDraft(SleepType.NAP, s.startAt, now, source = SleepSource.TIMER))) as SleepEvent
        assertFalse(stopped.isOpen)
        assertNull(env.repo.observeOpenSleep(BABY_ID).first())
        // a parada foi absorvida pela CREATE ainda não enviada
        val m = env.queue.snapshot().single()
        assertEquals(MutationOp.CREATE, m.op)
        assertTrue(m.data!!["end_at"]!!.toString().contains("2026-10-08T12:00:00Z"))
    }

    @Test fun `edicao de registro confirmado gera UPDATE so com campos alterados e base_version`() = runBlocking {
        val e = saved(env.repo.create(BABY_ID, BottleDraft(90, MilkType.FORMULA, now)))
        env.queue.markApplied(env.queue.snapshot().single().mutationId, 3)
        env.repo.update(e.id, BottleDraft(120, MilkType.FORMULA, now))
        val u = env.queue.snapshot().single()
        assertEquals(MutationOp.UPDATE, u.op)
        assertEquals(3, u.baseVersion)
        assertEquals(setOf("volume_ml"), u.data!!.keys)
        // sem mudança nenhuma não há mutação
        env.repo.update(e.id, BottleDraft(120, MilkType.FORMULA, now))
        assertEquals(1, env.queue.snapshot().size)
    }

    @Test fun `excluir registro confirmado faz tombstone local e enfileira DELETE`() = runBlocking {
        val e = saved(env.repo.create(BABY_ID, DiaperDraft(now, DiaperType.DIRTY)))
        env.queue.markApplied(env.queue.snapshot().single().mutationId, 1)
        assertTrue(env.repo.delete(e.id) is Outcome.Success)
        assertTrue(env.repo.observeTimeline(BABY_ID, 50).first().isEmpty())
        assertNotNull("tombstone permanece até o servidor confirmar", env.db.trackingDao().get(e.id))
        val d = env.queue.snapshot().single()
        assertEquals(MutationOp.DELETE, d.op)
        assertEquals(1, d.baseVersion)
        assertNull(d.data)
        env.queue.markApplied(d.mutationId, 2)
        assertNull(env.db.trackingDao().get(e.id))
    }

    @Test fun `excluir registro nunca enviado descarta tudo sem DELETE`() = runBlocking {
        val e = saved(env.repo.create(BABY_ID, DiaperDraft(now, DiaperType.DIRTY)))
        env.repo.delete(e.id)
        assertTrue(env.queue.snapshot().isEmpty())
        assertNull(env.db.trackingDao().get(e.id))
    }

    @Test fun `somente leitura nao registra`() = runBlocking {
        env.seedBaby(role = Role.READ_ONLY)
        val r = env.repo.create(BABY_ID, DiaperDraft(now, DiaperType.WET))
        assertEquals(Rejection.ReadOnly, (r as SaveResult.Rejected).reason)
        assertTrue(env.queue.snapshot().isEmpty())
    }

    @Test fun `validacoes do contrato`() = runBlocking {
        fun issues(r: SaveResult<*>) = (r as SaveResult.Invalid).issues.map { it.field to it.code }
        assertTrue((EventField.SIDE to IssueCode.REQUIRED) in issues(env.repo.create(BABY_ID, BreastfeedingDraft(null, now.minusSeconds(60), now))))
        assertTrue((EventField.END to IssueCode.REQUIRED) in issues(env.repo.create(BABY_ID, BreastfeedingDraft(BreastSide.LEFT, now, null))))
        assertTrue((EventField.END to IssueCode.END_BEFORE_START) in issues(env.repo.create(BABY_ID, SleepDraft(SleepType.NAP, now, now.minusSeconds(1), source = SleepSource.MANUAL))))
        assertTrue((EventField.VOLUME to IssueCode.VOLUME_RANGE) in issues(env.repo.create(BABY_ID, BottleDraft(0, null, now))))
        assertTrue((EventField.START to IssueCode.FUTURE_TIME) in issues(env.repo.create(BABY_ID, DiaperDraft(now.plus(Duration.ofHours(2)), DiaperType.WET))))
        assertTrue(env.queue.snapshot().isEmpty())
    }

    @Test fun `milk_type so existe em mamadeira e feeding_type e imutavel`() = runBlocking {
        val breast = saved(env.repo.create(BABY_ID, BreastfeedingDraft(BreastSide.LEFT, now.minusSeconds(300), now))) as FeedingEvent
        assertNull(breast.milkType)
        assertNull(breast.volumeMl)
        val keys = env.queue.snapshot().single().data!!.keys
        assertFalse("milk_type" in keys || "volume_ml" in keys)
        val r = env.repo.update(breast.id, BottleDraft(100, null, now))
        assertEquals(Rejection.TypeMismatch, (r as SaveResult.Rejected).reason)
    }

    @Test fun `despertares seguem a sessao e saem antes dela na exclusao`() = runBlocking {
        env.clock.advance(Duration.ofHours(5))
        val sleep = saved(env.repo.create(BABY_ID, SleepDraft(SleepType.NIGHT, now.minus(Duration.ofHours(4)), now.minus(Duration.ofMinutes(10)), source = SleepSource.MANUAL)))
        env.queue.markApplied(env.queue.snapshot().single().mutationId, 1)
        val w = saved(env.repo.createWake(BABY_ID, WakeDraft(sleep.id, now.minus(Duration.ofHours(2)), now.minus(Duration.ofMinutes(105)))))
        env.queue.markApplied(env.queue.snapshot().single().mutationId, 1)
        assertEquals(listOf(w.id), env.repo.observeWakeEvents(sleep.id).first().map { it.id })
        // fora da sessão é inválido
        assertTrue(env.repo.createWake(BABY_ID, WakeDraft(sleep.id, now.minus(Duration.ofHours(9)), now.minus(Duration.ofHours(8)))) is SaveResult.Invalid)

        env.repo.delete(sleep.id)
        val ops = env.queue.snapshot().map { it.entityId to it.op }
        assertEquals(listOf(w.id to MutationOp.DELETE, sleep.id to MutationOp.DELETE), ops)
        assertTrue(env.repo.observeWakeEvents(sleep.id).first().isEmpty())
    }

    @Test fun `padroes inteligentes vem do ultimo registro`() = runBlocking {
        env.repo.create(BABY_ID, BottleDraft(150, MilkType.BREAST_MILK, now.minusSeconds(3600)))
        env.repo.create(BABY_ID, BreastfeedingDraft(BreastSide.RIGHT, now.minusSeconds(1200), now.minusSeconds(600)))
        val d = env.repo.quickDefaults(BABY_ID)
        assertEquals(150, d.lastBottleVolumeMl)
        assertEquals(MilkType.BREAST_MILK, d.lastMilkType)
        assertEquals(BreastSide.RIGHT, d.lastBreastSide)
        assertEquals(10, d.lastBreastMinutes)
    }

    @Test fun `sobreposicao de sono e detectada para aviso`() = runBlocking {
        env.repo.create(BABY_ID, SleepDraft(SleepType.NAP, now.minusSeconds(3600), now.minusSeconds(1800), source = SleepSource.MANUAL))
        assertTrue(env.repo.hasSleepOverlap(BABY_ID, now.minusSeconds(2400), now.minusSeconds(600), null))
        assertFalse(env.repo.hasSleepOverlap(BABY_ID, now.minusSeconds(1800), now, null))
    }

    @Test fun `janela de dia com horario de verao tem 23 h e inclui so eventos do dia`() = runBlocking {
        val ny = ZoneId.of("America/New_York")
        val window = app.nina.domain.tracking.DayWindow.of(LocalDate.of(2026, 3, 8), ny)
        assertEquals(Duration.ofHours(23), window.length)
        env.repo.create(BABY_ID, DiaperDraft(window.start.plusSeconds(60), DiaperType.WET))
        env.clock.now = window.end.plusSeconds(3600)
        env.repo.create(BABY_ID, DiaperDraft(window.end.plusSeconds(60), DiaperType.WET))
        assertEquals(1, env.repo.observeRange(BABY_ID, window.start, window.end).first().size)
    }
}
