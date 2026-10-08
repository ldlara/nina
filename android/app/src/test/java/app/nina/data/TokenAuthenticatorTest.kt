package app.nina.data

import app.nina.domain.model.AppError
import app.nina.domain.model.Outcome
import app.nina.domain.model.SessionState
import app.nina.domain.model.SignedOutReason
import app.nina.testutil.ApiHarness
import app.nina.testutil.Samples
import app.nina.testutil.cachedBabyEntity
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TokenAuthenticatorTest {
    private val h = ApiHarness(signedIn = true)

    @After fun tearDown() = h.shutdown()

    @Test fun `401 com token expirado rotaciona o refresh e repete a requisicao`() = runTest {
        h.enqueueProblem(401, "TOKEN_EXPIRED")
        h.enqueue(200, Samples.tokens("access-new", "refresh-new"))
        h.enqueue(200, """{"items":[]}""")

        val r = h.babies.refreshBabies()

        assertTrue(r is Outcome.Success)
        val first = h.server.takeRequest()
        assertEquals("/v1/babies", first.path)
        assertEquals("Bearer access-old", first.getHeader("Authorization"))

        val refresh = h.server.takeRequest()
        assertEquals("/v1/auth/refresh", refresh.path)
        assertNull("refresh é público", refresh.getHeader("Authorization"))
        val body = Json.parseToJsonElement(refresh.body.readUtf8()).jsonObject
        assertEquals("refresh-old", body.getValue("refresh_token").jsonPrimitive.content)
        assertEquals("dev-1", body.getValue("device_id").jsonPrimitive.content)

        val retry = h.server.takeRequest()
        assertEquals("/v1/babies", retry.path)
        assertEquals("Bearer access-new", retry.getHeader("Authorization"))
        assertEquals("refresh-new", h.tokenStore.stored?.refreshToken)
        assertTrue(h.session.state.value is SessionState.SignedIn)
    }

    @Test fun `refresh recusado encerra a sessao local sem apagar o cache`() = runTest {
        h.babyDao.upsert(cachedBabyEntity())
        h.enqueueProblem(401, "SESSION_REVOKED")
        h.enqueueProblem(401, "REFRESH_TOKEN_REUSED")

        val r = h.babies.refreshBabies()

        val error = (r as Outcome.Failure).error as AppError.Api
        assertEquals(401, error.httpStatus)
        assertNull(h.tokenStore.stored)
        assertEquals(SessionState.SignedOut(SignedOutReason.EXPIRED), h.session.state.value)
        // RF-001-A4: dados locais permanecem.
        assertEquals(1, h.babyDao.items.value.size)
        assertEquals(2, h.server.requestCount)
    }

    @Test fun `falha transitoria do refresh nao derruba a sessao`() = runTest {
        h.enqueueProblem(401, "TOKEN_EXPIRED")
        h.enqueueProblem(503, "UNAVAILABLE")

        val r = h.babies.refreshBabies()

        assertEquals(401, ((r as Outcome.Failure).error as AppError.Api).httpStatus)
        assertNotNull(h.tokenStore.stored)
        assertTrue(h.session.state.value is SessionState.SignedIn)
    }
}
