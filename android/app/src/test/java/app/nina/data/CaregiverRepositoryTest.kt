package app.nina.data

import app.nina.domain.model.AppError
import app.nina.domain.model.InvitableRole
import app.nina.domain.model.MembershipStatus
import app.nina.domain.model.Outcome
import app.nina.domain.model.Role
import app.nina.testutil.ApiHarness
import app.nina.testutil.Samples
import app.nina.testutil.cachedBabyEntity
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CaregiverRepositoryTest {
    private val h = ApiHarness(signedIn = true)

    @After fun tearDown() = h.shutdown()

    @Test fun `lista cuidadores e convites e tolera papel e status novos`() = runTest {
        h.enqueue(
            200,
            """{"items":[
                ${Samples.membership("m-1", userId = "u-1", name = "Ana", role = "OWNER")},
                ${Samples.membership("m-2", userId = null, name = null, email = "avo@example.org", status = "PENDING")},
                ${Samples.membership("m-3", role = "SITTER", status = "SNOOZED")}
            ]}""",
        )

        assertTrue(h.caregivers.refresh("b-1") is Outcome.Success)

        val list = h.caregivers.observeMembers("b-1").first()
        assertEquals(3, list.size)
        assertEquals(Role.OWNER, list[0].role)
        assertEquals(MembershipStatus.PENDING, list[1].status)
        assertNull(list[1].userId)
        assertEquals("avo@example.org", list[1].invitedEmail)
        assertEquals(Role.UNRECOGNIZED, list[2].role)
        assertEquals(MembershipStatus.UNRECOGNIZED, list[2].status)
        assertEquals("/v1/babies/b-1/caregivers", h.server.takeRequest().path)
    }

    @Test fun `convidar envia e-mail e papel em maiusculas com Idempotency-Key`() = runTest {
        h.enqueue(201, Samples.membership("m-9", userId = null, name = null, email = "avo@example.org", status = "PENDING"))

        val r = h.caregivers.invite("b-1", " avo@example.org ", InvitableRole.READ_ONLY)

        assertEquals(MembershipStatus.PENDING, (r as Outcome.Success).value.status)
        val req = h.server.takeRequest()
        assertEquals("/v1/babies/b-1/invitations", req.path)
        assertEquals("id-1", req.getHeader("Idempotency-Key"))
        val body = Json.parseToJsonElement(req.body.readUtf8()).jsonObject
        assertEquals("avo@example.org", body.getValue("email").jsonPrimitive.content)
        assertEquals("READ_ONLY", body.getValue("role").jsonPrimitive.content)
        assertEquals(1, h.membershipDao.items.value.size)
    }

    @Test fun `reconvite de quem ja e membro devolve ALREADY_MEMBER`() = runTest {
        h.enqueueProblem(409, "ALREADY_MEMBER")
        val r = h.caregivers.invite("b-1", "avo@example.org", InvitableRole.CAREGIVER)
        assertEquals("ALREADY_MEMBER", ((r as Outcome.Failure).error as AppError.Api).code)
    }

    @Test fun `papel nao enviavel UNRECOGNIZED e barrado antes da rede`() = runTest {
        val r = h.caregivers.invite("b-1", "a@b.co", InvitableRole.UNRECOGNIZED)
        assertTrue((r as Outcome.Failure).error is AppError.Unexpected)
        assertEquals(0, h.server.requestCount)
    }

    @Test fun `mudar papel usa PATCH com role`() = runTest {
        h.enqueue(200, Samples.membership("m-2", role = "READ_ONLY"))
        h.caregivers.changeRole("b-1", "m-2", InvitableRole.READ_ONLY)
        val req = h.server.takeRequest()
        assertEquals("PATCH", req.method)
        assertEquals("/v1/babies/b-1/caregivers/m-2", req.path)
        assertEquals("READ_ONLY", Json.parseToJsonElement(req.body.readUtf8()).jsonObject.getValue("role").jsonPrimitive.content)
    }

    @Test fun `remover cuidador apaga o vinculo do cache`() = runTest {
        h.enqueue(200, """{"items":[${Samples.membership("m-1")},${Samples.membership("m-2")}]}""")
        h.caregivers.refresh("b-1")
        h.enqueue(204)

        val r = h.caregivers.remove("b-1", "m-2")

        assertTrue(r is Outcome.Success)
        assertEquals(listOf("m-1"), h.membershipDao.items.value.map { it.id })
        h.server.takeRequest()
        assertEquals("DELETE", h.server.takeRequest().method)
    }

    @Test fun `listar com vinculo revogado limpa bebe e cuidadores locais`() = runTest {
        h.babyDao.upsert(cachedBabyEntity())
        h.membershipDao.upsert(app.nina.data.local.MembershipEntity("m-1", "b-1", null, null, null, "OWNER", "ACTIVE", "2026-10-08T17:00:00Z", null, null))
        h.enqueueProblem(403, "ACCESS_REVOKED")

        h.caregivers.refresh("b-1")

        assertTrue(h.babyDao.items.value.isEmpty())
        assertTrue(h.membershipDao.items.value.isEmpty())
    }

    @Test fun `convidado ve previa aceita e o bebe entra no cache`() = runTest {
        h.enqueue(200, """{"inviter_display_name":"Ana","baby_label":"N.","role":"CAREGIVER","expires_at":"2026-10-15T17:00:00Z","visible_data":["timeline"]}""")
        val preview = (h.caregivers.inspectInvitation("tok") as Outcome.Success).value
        assertEquals("Ana", preview.inviterDisplayName)
        assertEquals(InvitableRole.CAREGIVER, preview.role)

        h.enqueue(200, Samples.baby(role = "CAREGIVER"))
        val baby = (h.caregivers.acceptInvitation("tok") as Outcome.Success).value
        assertEquals(Role.CAREGIVER, baby.myRole)
        assertEquals(1, h.babyDao.items.value.size)

        val inspect = h.server.takeRequest()
        assertEquals("/v1/invitations/inspect", inspect.path)
        assertEquals("tok", Json.parseToJsonElement(inspect.body.readUtf8()).jsonObject.getValue("token").jsonPrimitive.content)
        assertEquals("/v1/invitations/accept", h.server.takeRequest().path)
    }

    @Test fun `convite invalido responde 404 uniforme`() = runTest {
        h.enqueueProblem(404, "NOT_FOUND")
        val r = h.caregivers.inspectInvitation("xxx")
        assertEquals("NOT_FOUND", ((r as Outcome.Failure).error as AppError.Api).code)
    }

    @Test fun `recusar convite chama decline`() = runTest {
        h.enqueue(204)
        assertTrue(h.caregivers.declineInvitation("tok") is Outcome.Success)
        assertEquals("/v1/invitations/decline", h.server.takeRequest().path)
    }
}
