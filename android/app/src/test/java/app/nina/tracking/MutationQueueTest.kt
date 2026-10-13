package app.nina.tracking

import androidx.test.core.app.ApplicationProvider
import app.nina.data.local.MutationEntity
import app.nina.domain.model.BreastSide
import app.nina.domain.model.DiaperType
import app.nina.domain.model.MutationOp
import app.nina.domain.model.SleepSource
import app.nina.domain.model.SleepType
import app.nina.domain.tracking.BreastfeedingDraft
import app.nina.domain.tracking.DiaperDraft
import app.nina.domain.tracking.MutationQueue
import app.nina.domain.tracking.MutationStatus
import app.nina.domain.tracking.RetryBackoff
import app.nina.domain.tracking.SaveResult
import app.nina.domain.tracking.SleepDraft
import app.nina.domain.tracking.SyncState
import app.nina.testutil.BABY_ID
import app.nina.testutil.TrackingEnv
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Duration
import java.time.Instant

/** Prova da fila de mutações (RF-046-A3..A5, ADR-0003): idempotência por UUID, ordem, retry e recuperação. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], application = android.app.Application::class)
class MutationQueueTest {
    private lateinit var env: TrackingEnv
    private val now get() = env.clock.now

    @Before fun setUp() {
        env = TrackingEnv(ApplicationProvider.getApplicationContext())
        runBlocking { env.seedBaby() }
    }

    @After fun tearDown() = env.close()

    private suspend fun diaper(minutesAgo: Long = 0): String {
        val r = env.repo.create(BABY_ID, DiaperDraft(now.minus(Duration.ofMinutes(minutesAgo)), DiaperType.WET)) as SaveResult.Saved
        return r.value.id
    }

    private fun mutation(id: String, seq: Long = 0) = MutationEntity(
        seq = seq, mutationId = id, babyId = BABY_ID, entityType = "DIAPER_EVENT", entityId = "e-$id", op = "CREATE", baseVersion = 0,
        clientCreatedAt = 1L, deviceId = "d", payload = "{}", status = "PENDING", attempts = 0, nextAttemptAt = 0, lastErrorCode = null,
    )

    // ---- idempotência por UUID ---------------------------------------------------------------------------------------
    @Test fun `mesmo mutation_id nao duplica a mutacao na fila`() = runBlocking {
        val dao = env.db.mutationDao()
        val first = dao.insert(mutation("aaaaaaaa-0000-4000-8000-000000000001"))
        val second = dao.insert(mutation("aaaaaaaa-0000-4000-8000-000000000001"))
        assertTrue(first > 0)
        assertEquals("reinserir o mesmo UUID é ignorado (-1)", -1L, second)
        assertEquals(1, dao.all().size)
    }

    @Test fun `confirmar duas vezes a mesma mutacao e idempotente (DUPLICATE)`() = runBlocking {
        val id = diaper()
        val m = env.queue.snapshot().single()
        env.queue.markApplied(m.mutationId, serverVersion = 1)
        env.queue.markApplied(m.mutationId, serverVersion = 1) // reenvio: servidor responde DUPLICATE
        assertTrue(env.queue.snapshot().isEmpty())
        val e = env.repo.observeEvent(id).first()!!
        assertEquals(1, e.version)
        assertEquals(SyncState.SYNCED, e.syncState)
    }

    // ---- ordem -------------------------------------------------------------------------------------------------------
    @Test fun `o lote respeita a ordem de insercao e carrega os campos do contrato`() = runBlocking {
        val a = diaper(30)
        val b = diaper(20)
        val c = diaper(10)
        val batch = env.queue.nextBatch(now)
        assertEquals(listOf(a, b, c), batch.map { it.entityId })
        assertEquals(batch.map { it.seq }.sorted(), batch.map { it.seq })
        batch.forEach {
            assertEquals(MutationOp.CREATE, it.op)
            assertEquals(0, it.baseVersion)
            assertEquals(app.nina.testutil.DEVICE_ID, it.deviceId)
            assertEquals(now, it.clientCreatedAt)
            assertTrue(it.mutationId.matches(Regex("[0-9a-f-]{36}")))
        }
        assertEquals(3, batch.map { it.mutationId }.toSet().size)
    }

    @Test fun `lote tem no maximo 100 mutacoes`() = runBlocking {
        repeat(105) { diaper() }
        assertEquals(100, env.queue.nextBatch(now).size)
        assertEquals(105, env.queue.observePendingCount().first())
    }

    @Test fun `enquanto um lote esta em voo nenhum novo lote sai`() = runBlocking {
        diaper(); diaper()
        val batch = env.queue.nextBatch(now)
        env.queue.markInFlight(batch.map { it.mutationId })
        assertTrue(env.queue.nextBatch(now).isEmpty())
        // reinício do app com mutações em voo: voltam à fila na mesma ordem (RF-046-A5)
        env.queue.releaseInFlight()
        assertEquals(batch.map { it.mutationId }, env.queue.nextBatch(now).map { it.mutationId })
    }

    // ---- retry -------------------------------------------------------------------------------------------------------
    @Test fun `retry marca tentativa e backoff e segura as mutacoes mais novas (ordem preservada)`() = runBlocking {
        val first = diaper(10)
        val second = diaper(5)
        val head = env.queue.nextBatch(now).first()
        val next = RetryBackoff.nextAttemptAt(now, attemptsSoFar = 0)
        env.queue.markRetry(head.mutationId, "TRANSIENT", next)

        val marked = env.queue.snapshot().first { it.mutationId == head.mutationId }
        assertEquals(MutationStatus.RETRY, marked.status)
        assertEquals(1, marked.attempts)
        assertEquals("TRANSIENT", marked.lastErrorCode)
        assertEquals(next, marked.nextAttemptAt)
        assertEquals(now.plusSeconds(5), next)

        // antes do prazo: nada sai, nem a segunda (não passa na frente da primeira)
        assertTrue(env.queue.nextBatch(now.plusSeconds(4)).isEmpty())
        assertEquals(2, env.queue.observePendingCount().first())

        // no prazo: a primeira volta primeiro, seguida da segunda
        val after = env.queue.nextBatch(now.plusSeconds(5))
        assertEquals(listOf(first, second), after.map { it.entityId })
        assertEquals(1, after.first().attempts)

        // "Tentar agora" zera o backoff
        env.queue.retryNow()
        assertEquals(2, env.queue.nextBatch(now).size)
    }

    @Test fun `backoff e exponencial com teto e respeita Retry-After`() {
        assertEquals(Duration.ofSeconds(5), RetryBackoff.delayFor(0))
        assertEquals(Duration.ofSeconds(10), RetryBackoff.delayFor(1))
        assertEquals(Duration.ofSeconds(40), RetryBackoff.delayFor(3))
        assertEquals(Duration.ofMinutes(15), RetryBackoff.delayFor(30))
        assertEquals(Duration.ofSeconds(120), RetryBackoff.delayFor(0, retryAfter = Duration.ofSeconds(120)))
    }

    // ---- recusa definitiva -------------------------------------------------------------------------------------------
    @Test fun `recusa definitiva sai do fluxo de envio e marca o registro como FAILED`() = runBlocking {
        val id = diaper()
        val m = env.queue.snapshot().single()
        env.queue.markRejected(m.mutationId, "VALIDATION_FAILED")
        assertTrue(env.queue.nextBatch(now).isEmpty())
        assertEquals(0, env.queue.observePendingCount().first())
        assertEquals(1, env.queue.observeRejectedCount().first())
        assertEquals(SyncState.FAILED, env.repo.observeEvent(id).first()!!.syncState)
    }

    // ---- aplicada ----------------------------------------------------------------------------------------------------
    @Test fun `aplicar CREATE confirma versao e reajusta a base das mutacoes seguintes`() = runBlocking {
        val id = diaper()
        val create = env.queue.snapshot().single()
        env.queue.markInFlight(listOf(create.mutationId)) // CREATE em voo: a edição vira UPDATE separado
        env.repo.update(id, DiaperDraft(now, DiaperType.DIRTY))
        val update = env.queue.snapshot().last()
        assertEquals(MutationOp.UPDATE, update.op)
        assertEquals(0, update.baseVersion)

        env.queue.markApplied(create.mutationId, serverVersion = 1)
        assertEquals("base_version do UPDATE passa a ser a versão que o servidor atribuiu", 1, env.queue.snapshot().single().baseVersion)
        assertEquals("ainda há UPDATE pendente", SyncState.PENDING, env.repo.observeEvent(id).first()!!.syncState)
        env.queue.markApplied(update.mutationId, serverVersion = 2)
        assertEquals(SyncState.SYNCED, env.repo.observeEvent(id).first()!!.syncState)
        assertEquals(2, env.repo.observeEvent(id).first()!!.version)
    }

    // ---- durabilidade (RF-046-A4) ------------------------------------------------------------------------------------
    @Test fun `a fila sobrevive a reabrir o banco`() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        fun open() = androidx.room.Room.databaseBuilder(context, app.nina.data.local.NinaDatabase::class.java, "durable.db")
            .allowMainThreadQueries().build()
        val db1 = open()
        db1.babyDao().upsert(app.nina.testutil.babyEntity())
        val repo1 = app.nina.data.repository.DefaultTrackingRepository(
            db1, db1.trackingDao(), db1.mutationDao(), db1.babyDao(), env.clock, { "dev" },
            app.nina.domain.tracking.EventValidator(env.clock), env::nextId,
        )
        val saved = (repo1.create(BABY_ID, SleepDraft(SleepType.NAP, now.minusSeconds(600), null, source = SleepSource.TIMER)) as SaveResult.Saved).value
        db1.close() // app encerrado com mutação pendente

        val db2 = open()
        val pending = app.nina.data.repository.RoomMutationQueue(db2, db2.mutationDao(), db2.trackingDao()).nextBatch(now)
        assertEquals(listOf(saved.id), pending.map { it.entityId })
        assertEquals(saved.id, db2.trackingDao().getOpenSleep(BABY_ID)!!.id)
        db2.close()
    }

    @Test fun `mamada pendente editada reescreve a propria CREATE sem nova mutacao`() = runBlocking {
        val saved = (env.repo.create(BABY_ID, BreastfeedingDraft(BreastSide.LEFT, now.minusSeconds(600), now)) as SaveResult.Saved).value
        env.repo.update(saved.id, BreastfeedingDraft(BreastSide.BOTH, now.minusSeconds(600), now, "ok"))
        val q = env.queue.snapshot()
        assertEquals(1, q.size)
        assertEquals(MutationOp.CREATE, q.single().op)
        assertEquals("BOTH", q.single().data!!["side"]!!.toString().trim('"'))
        assertEquals(Instant.parse("2026-10-08T12:00:00Z"), now)
    }
}
