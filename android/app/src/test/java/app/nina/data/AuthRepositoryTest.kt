package app.nina.data

import app.nina.domain.auth.SocialCredential
import app.nina.domain.model.AppError
import app.nina.domain.model.ConsentAcceptance
import app.nina.domain.model.ConsentPurpose
import app.nina.domain.model.IdentityProvider
import app.nina.domain.model.Outcome
import app.nina.domain.model.SessionState
import app.nina.domain.model.SignedOutReason
import app.nina.testutil.ApiHarness
import app.nina.testutil.Samples
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class AuthRepositoryTest {
    private val h = ApiHarness()

    @After fun tearDown() = h.shutdown()

    private val consents = listOf(
        ConsentAcceptance(ConsentPurpose.TERMS_OF_USE, "1.0.0"),
        ConsentAcceptance(ConsentPurpose.PRIVACY_POLICY, "1.0.0"),
    )

    @Test fun `cadastro envia contrato correto e nao abre sessao (202 uniforme)`() = runTest {
        h.enqueue(202, """{"status":"VERIFICATION_PENDING","resend_after_seconds":60}""")

        val r = h.auth.register(" ana@example.org ", "s3nha-longa", " Ana ", consents)

        assertEquals(60, (r as Outcome.Success).value.resendAfterSeconds)
        assertTrue(h.session.state.value is SessionState.SignedOut)
        assertNull(h.tokenStore.stored)

        val req = h.server.takeRequest()
        assertEquals("/v1/auth/register", req.path)
        assertNull("endpoint público não envia Authorization", req.getHeader("Authorization"))
        assertNull("cabeçalho interno não vaza", req.getHeader("X-Nina-No-Auth"))
        assertEquals("id-1", req.getHeader("Idempotency-Key"))
        assertEquals("pt-BR", req.getHeader("Accept-Language"))
        val body = Json.parseToJsonElement(req.body.readUtf8()).jsonObject
        assertEquals("ana@example.org", body.getValue("email").jsonPrimitive.content)
        assertEquals("Ana", body.getValue("display_name").jsonPrimitive.content)
        assertEquals("pt-BR", body.getValue("locale").jsonPrimitive.content)
        assertEquals("America/Sao_Paulo", body.getValue("timezone").jsonPrimitive.content)
        val sent = body.getValue("consents").jsonArray.map { it.jsonObject }
        assertEquals(listOf("TERMS_OF_USE", "PRIVACY_POLICY"), sent.map { it.getValue("purpose_key").jsonPrimitive.content })
        assertEquals("1.0.0", sent[0].getValue("document_version").jsonPrimitive.content)
    }

    @Test fun `reenvio repete o cadastro pendente com nova Idempotency-Key`() = runTest {
        h.enqueue(202, """{"status":"VERIFICATION_PENDING"}""")
        h.enqueue(202, """{"status":"VERIFICATION_PENDING","resend_after_seconds":30}""")
        h.auth.register("ana@example.org", "s3nha-longa", null, consents)
        val first = h.server.takeRequest()

        val r = h.auth.resendVerification()

        assertEquals(30, (r as Outcome.Success).value.resendAfterSeconds)
        val second = h.server.takeRequest()
        assertNotEquals(first.getHeader("Idempotency-Key"), second.getHeader("Idempotency-Key"))
        assertEquals(first.body.readUtf8(), second.body.readUtf8())
    }

    @Test fun `reenvio sem cadastro pendente falha sem chamar a rede`() = runTest {
        val r = h.auth.resendVerification()
        assertTrue((r as Outcome.Failure).error is AppError.Unexpected)
        assertEquals(0, h.server.requestCount)
    }

    @Test fun `confirmacao de e-mail abre a sessao e guarda os tokens`() = runTest {
        h.enqueue(200, Samples.tokens("acc", "ref"))

        val r = h.auth.verifyEmail("ana@example.org", " 123456 ")

        assertEquals("ana@example.org", (r as Outcome.Success).value.email)
        assertEquals("acc", h.tokenStore.stored?.accessToken)
        assertEquals("ref", h.tokenStore.stored?.refreshToken)
        assertTrue(h.session.state.value is SessionState.SignedIn)
        val body = Json.parseToJsonElement(h.server.takeRequest().body.readUtf8()).jsonObject
        assertEquals("123456", body.getValue("code").jsonPrimitive.content)
        val device = body.getValue("device").jsonObject
        assertEquals("ANDROID", device.getValue("platform").jsonPrimitive.content)
        assertEquals("dev-1", device.getValue("device_id").jsonPrimitive.content)
    }

    @Test fun `login com credencial invalida devolve code generico e nao cria sessao`() = runTest {
        h.enqueueProblem(401, "INVALID_CREDENTIALS")

        val r = h.auth.login("ana@example.org", "errada")

        val error = (r as Outcome.Failure).error as AppError.Api
        assertEquals(401, error.httpStatus)
        assertEquals("INVALID_CREDENTIALS", error.code)
        assertEquals("01J9Z3Q8M2T6V4X1B7N5C0D8EF", error.requestId)
        assertNull(h.tokenStore.stored)
        // 401 em endpoint público não aciona refresh: uma única chamada.
        assertEquals(1, h.server.requestCount)
    }

    @Test fun `erros de campo do servidor sao preservados`() = runTest {
        h.enqueueProblem(400, "VALIDATION_FAILED", """[{"field":"password","code":"PASSWORD_POLICY"}]""")

        val r = h.auth.register("ana@example.org", "123", null, consents)

        val error = (r as Outcome.Failure).error as AppError.Api
        assertEquals("VALIDATION_FAILED", error.code)
        assertEquals("password", error.fieldErrors.single().field)
        assertEquals("PASSWORD_POLICY", error.fieldErrors.single().code)
    }

    @Test fun `corpo de erro fora do padrao usa HTTP status como code`() = runTest {
        h.enqueue(503, "<html>bad gateway</html>", "text/html")
        val r = h.auth.login("a@b.co", "x")
        assertEquals("HTTP_503", ((r as Outcome.Failure).error as AppError.Api).code)
    }

    @Test fun `login social usa endpoint do provedor e conflito de identidade e reportado`() = runTest {
        h.enqueueProblem(409, "IDENTITY_LINK_REQUIRED")
        val cred = SocialCredential(IdentityProvider.GOOGLE, "id-token", "nonce-1", givenName = "Ana")

        val r = h.auth.socialLogin(cred, consents)

        assertEquals("IDENTITY_LINK_REQUIRED", ((r as Outcome.Failure).error as AppError.Api).code)
        val req = h.server.takeRequest()
        assertEquals("/v1/auth/google", req.path)
        val body = Json.parseToJsonElement(req.body.readUtf8()).jsonObject
        assertEquals("id-token", body.getValue("id_token").jsonPrimitive.content)
        assertEquals("nonce-1", body.getValue("nonce").jsonPrimitive.content)
        assertEquals(2, body.getValue("consents").jsonArray.size)
    }

    @Test fun `login apple vai para auth apple e sem aceite nao envia consents`() = runTest {
        h.enqueue(200, Samples.tokens())
        h.auth.socialLogin(SocialCredential(IdentityProvider.APPLE, "t", "n"), emptyList())
        val req = h.server.takeRequest()
        assertEquals("/v1/auth/apple", req.path)
        assertFalse("consents" in Json.parseToJsonElement(req.body.readUtf8()).jsonObject)
        assertTrue(h.session.state.value is SessionState.SignedIn)
    }

    @Test fun `falha de rede vira AppError Network`() = runTest {
        h.shutdown()
        val r = h.auth.login("ana@example.org", "x")
        assertEquals(AppError.Network, (r as Outcome.Failure).error)
    }

    @Test fun `logout chama a API com bearer limpa tokens e dados locais`() = runTest {
        h.enqueue(200, Samples.tokens("acc", "ref"))
        h.auth.login("ana@example.org", "x")
        h.server.takeRequest()
        h.enqueue(204)

        h.auth.logout()

        val req = h.server.takeRequest()
        assertEquals("/v1/auth/logout", req.path)
        assertEquals("Bearer acc", req.getHeader("Authorization"))
        assertNull(h.tokenStore.stored)
        assertEquals(SessionState.SignedOut(SignedOutReason.NONE), h.session.state.value)
        assertEquals(1, h.clearedLocalData)
    }

    @Test fun `consentimento de responsavel legal ja concedido nao e pedido de novo`() = runTest {
        h.enqueue(
            200,
            """{"current":[{"id":"c1","purpose_key":"CHILD_DATA_GUARDIAN","document_version":"1.0.0","status":"GRANTED","granted_at":"2026-10-08T17:00:00Z"}],"pending_required":[]}""",
        )
        val r = h.auth.guardianConsentStatus()
        assertTrue((r as Outcome.Success).value.alreadyGranted)
    }

    @Test fun `consentimento de responsavel legal pendente usa a versao do documento vigente`() = runTest {
        h.enqueue(200, """{"current":[],"pending_required":[]}""")
        h.enqueue(200, """{"items":[{"purpose_key":"CHILD_DATA_GUARDIAN","version":"2.1.0","url":"https://example.invalid/g","required":true}]}""")
        val status = (h.auth.guardianConsentStatus() as Outcome.Success).value
        assertFalse(status.alreadyGranted)
        assertEquals("2.1.0", status.documentVersion)

        h.enqueue(201, """{"id":"c2","purpose_key":"CHILD_DATA_GUARDIAN","document_version":"2.1.0","status":"GRANTED","granted_at":"2026-10-08T17:00:00Z"}""")
        assertTrue(h.auth.grantGuardianConsent("2.1.0") is Outcome.Success)
        h.server.takeRequest(); h.server.takeRequest()
        val post = h.server.takeRequest()
        assertEquals("/v1/me/consents", post.path)
        val body = Json.parseToJsonElement(post.body.readUtf8()).jsonObject
        assertEquals("CHILD_DATA_GUARDIAN", body.getValue("purpose_key").jsonPrimitive.content)
        assertEquals("GRANTED", body.getValue("status").jsonPrimitive.content)
        assertEquals("ONBOARDING", body.getValue("source").jsonPrimitive.content)
    }
}
