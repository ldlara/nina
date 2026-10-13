package app.nina.data

import app.nina.data.repository.DefaultBabyRepository
import app.nina.domain.model.AppError
import app.nina.domain.model.Baby
import app.nina.domain.model.BabyDraft
import app.nina.domain.model.Outcome
import app.nina.domain.model.Role
import app.nina.domain.model.Sex
import app.nina.testutil.ApiHarness
import app.nina.testutil.Samples
import app.nina.testutil.cachedBabyEntity
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalDate

class BabyRepositoryTest {
    private val h = ApiHarness(signedIn = true)

    @After fun tearDown() = h.shutdown()

    private suspend fun currentBaby(): Baby {
        h.babyDao.upsert(cachedBabyEntity())
        return h.babies.observeBaby("b-1").first()!!
    }

    @Test fun `lista guarda no cache e a UI le do Room incluindo age_calculation`() = runTest {
        h.enqueue(200, """{"items":[${Samples.baby()},${Samples.baby(id = "b-2", name = "Leo", role = "READ_ONLY", dueDate = null, corrected = "null")}]}""")

        assertTrue(h.babies.refreshBabies() is Outcome.Success)

        val list = h.babies.observeBabies().first()
        assertEquals(listOf("Nina", "Leo"), list.map { it.displayName })
        assertEquals(257, list[0].ageCalculation?.correctedDays)
        assertNull("null = não se aplica", list[1].ageCalculation?.correctedDays)
        assertEquals(Role.READ_ONLY, list[1].myRole)
        assertEquals(LocalDate.of(2026, 10, 8), list[0].ageAsOf)
        assertEquals("Bearer access-old", h.server.takeRequest().getHeader("Authorization"))
    }

    @Test fun `sem rede o cache e mantido`() = runTest {
        h.babyDao.upsert(cachedBabyEntity())
        h.shutdown()

        val r = h.babies.refreshBabies()

        assertEquals(AppError.Network, (r as Outcome.Failure).error)
        assertEquals(1, h.babies.observeBabies().first().size)
    }

    @Test fun `lista do servidor remove do cache bebes sem acesso`() = runTest {
        h.babyDao.upsert(cachedBabyEntity("old"))
        h.enqueue(200, """{"items":[${Samples.baby()}]}""")
        h.babies.refreshBabies()
        assertEquals(listOf("b-1"), h.babies.observeBabies().first().map { it.id })
    }

    @Test fun `criar bebe envia snake_case com id e Idempotency-Key e guarda a idade devolvida`() = runTest {
        h.enqueue(201, Samples.baby(id = "id-1"))

        val r = h.babies.createBaby(
            BabyDraft(" Nina ", LocalDate.of(2026, 1, 10), LocalDate.of(2026, 1, 24), Sex.FEMALE, "America/Sao_Paulo"),
        )

        val baby = (r as Outcome.Success).value
        assertEquals(271, baby.ageCalculation?.chronologicalDays)
        assertEquals(1, h.babyDao.items.value.size)
        val req = h.server.takeRequest()
        assertEquals("POST", req.method)
        assertEquals("/v1/babies", req.path)
        assertEquals("id-2", req.getHeader("Idempotency-Key"))
        val body = Json.parseToJsonElement(req.body.readUtf8()).jsonObject
        assertEquals("id-1", body.getValue("id").jsonPrimitive.content)
        assertEquals("Nina", body.getValue("display_name").jsonPrimitive.content)
        assertEquals("2026-01-10", body.getValue("birth_date").jsonPrimitive.content)
        assertEquals("2026-01-24", body.getValue("due_date").jsonPrimitive.content)
        assertEquals("FEMALE", body.getValue("sex").jsonPrimitive.content)
    }

    @Test fun `consentimento ausente na criacao (403) e propagado com code`() = runTest {
        h.enqueueProblem(403, "CONSENT_REQUIRED")
        val r = h.babies.createBaby(BabyDraft("Nina", LocalDate.of(2026, 1, 10), null, null, "America/Sao_Paulo"))
        assertEquals("CONSENT_REQUIRED", ((r as Outcome.Failure).error as AppError.Api).code)
        assertTrue(h.babyDao.items.value.isEmpty())
    }

    @Test fun `editar envia merge-patch so com campos alterados e If-Match da versao`() = runTest {
        val current = currentBaby()
        h.enqueue(200, Samples.baby(name = "Nininha", version = 4))

        val r = h.babies.updateBaby(
            current,
            BabyDraft("Nininha", current.birthDate, current.dueDate, current.sex, current.timezone),
        )

        assertEquals(4, (r as Outcome.Success).value.version)
        val req = h.server.takeRequest()
        assertEquals("PATCH", req.method)
        assertEquals("/v1/babies/b-1", req.path)
        assertEquals("\"3\"", req.getHeader("If-Match"))
        assertTrue(req.getHeader("Content-Type")!!.startsWith("application/merge-patch+json"))
        val body = Json.parseToJsonElement(req.body.readUtf8()).jsonObject
        assertEquals(setOf("display_name"), body.keys)
        assertEquals("Nininha", h.babyDao.items.value.single().displayName)
    }

    @Test fun `remover data prevista envia null explicito`() = runTest {
        val current = currentBaby()
        h.enqueue(200, Samples.baby(dueDate = null, corrected = "null", version = 4))

        h.babies.updateBaby(current, BabyDraft("Nina", current.birthDate, null, null, current.timezone))

        val body = Json.parseToJsonElement(h.server.takeRequest().body.readUtf8()).jsonObject
        assertEquals(setOf("due_date"), body.keys)
        assertEquals(JsonNull, body.getValue("due_date"))
    }

    @Test fun `sem alteracoes nao chama a API`() = runTest {
        val current = currentBaby()
        val r = h.babies.updateBaby(current, BabyDraft(current.displayName, current.birthDate, current.dueDate, current.sex, current.timezone))
        assertTrue(r is Outcome.Success)
        assertEquals(0, h.server.requestCount)
    }

    @Test fun `conflito de versao 412 recarrega o bebe do servidor`() = runTest {
        val current = currentBaby()
        h.enqueueProblem(412, "VERSION_CONFLICT")
        h.enqueue(200, Samples.baby(name = "Nome do outro cuidador", version = 5))

        val r = h.babies.updateBaby(current, BabyDraft("Meu nome", current.birthDate, current.dueDate, current.sex, current.timezone))

        assertEquals("VERSION_CONFLICT", ((r as Outcome.Failure).error as AppError.Api).code)
        assertEquals("Nome do outro cuidador", h.babyDao.items.value.single().displayName)
        assertEquals(5, h.babyDao.items.value.single().version)
    }

    @Test fun `caregiver recebe 403 FORBIDDEN_ROLE ao editar`() = runTest {
        h.babyDao.upsert(cachedBabyEntity(role = "CAREGIVER"))
        val current = h.babies.observeBaby("b-1").first()!!
        h.enqueueProblem(403, "FORBIDDEN_ROLE")

        val r = h.babies.updateBaby(current, BabyDraft("Outro", current.birthDate, current.dueDate, current.sex, current.timezone))

        assertEquals("FORBIDDEN_ROLE", ((r as Outcome.Failure).error as AppError.Api).code)
        assertEquals("Nina", h.babyDao.items.value.single().displayName)
    }

    @Test fun `acesso revogado apaga bebe e vinculos do cache`() = runTest {
        h.babyDao.upsert(cachedBabyEntity())
        h.enqueueProblem(403, "ACCESS_REVOKED")
        h.babies.refreshBaby("b-1")
        assertTrue(h.babyDao.items.value.isEmpty())
    }

    @Test fun `reference-data expoe janela de idade corrigida da politica`() = runTest {
        h.enqueue(
            200,
            """{"version":"1","diaper_types":["WET"],"feeding_types":["BOTTLE"],"milk_types":["FORMULA"],"sleep_methods":[],
                "policies":{"corrected_age_max_months":24,"deletion_grace_days":7},"limits":{"invitation_ttl_days":7}}""",
        )
        val ref = (h.babies.referenceData() as Outcome.Success).value
        assertEquals(24, ref.correctedAgeMaxMonths)
        assertEquals(7, ref.invitationTtlDays)
    }

    @Test fun `buildMergePatch cobre todos os campos`() {
        val base = Baby(
            "b", "A", LocalDate.of(2026, 1, 1), LocalDate.of(2026, 1, 10), Sex.MALE, "UTC", Role.OWNER, 1, null, null,
            java.time.Instant.EPOCH, java.time.Instant.EPOCH,
        )
        val patch = DefaultBabyRepository.buildMergePatch(
            base,
            BabyDraft("B", LocalDate.of(2026, 1, 2), null, null, "America/Sao_Paulo"),
        )
        assertEquals(setOf("display_name", "birth_date", "due_date", "sex", "timezone"), patch.keys)
        assertEquals(JsonNull, patch.getValue("sex"))
    }
}
