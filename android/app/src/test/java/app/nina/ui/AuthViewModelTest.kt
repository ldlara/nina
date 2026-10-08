package app.nina.ui

import app.nina.R
import app.cash.turbine.test
import app.nina.domain.auth.SocialAuthResult
import app.nina.domain.auth.SocialCredential
import app.nina.domain.model.AppError
import app.nina.domain.model.ConsentPurpose
import app.nina.domain.model.IdentityProvider
import app.nina.domain.model.Outcome
import app.nina.testutil.FakeAuthRepository
import app.nina.testutil.FakeSocialProvider
import app.nina.testutil.MainDispatcherRule
import app.nina.ui.auth.AuthEvent
import app.nina.ui.auth.AuthMode
import app.nina.ui.auth.AuthViewModel
import app.nina.ui.auth.LegalState
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class AuthViewModelTest {
    @get:Rule val main = MainDispatcherRule()

    private val auth = FakeAuthRepository()
    private val social = FakeSocialProvider()
    private fun vm() = AuthViewModel(auth, social) { "nonce-fixo" }

    @Test fun `carrega versoes dos documentos legais ao iniciar`() {
        val vm = vm()
        assertEquals("1.0.0", vm.state.value.termsVersion)
        assertTrue(vm.state.value.legal is LegalState.Loaded)
    }

    @Test fun `falha ao carregar documentos bloqueia o estado e permite tentar de novo`() {
        auth.legal = Outcome.Failure(AppError.Network)
        val vm = vm()
        assertEquals(LegalState.Failed, vm.state.value.legal)
        auth.legal = FakeAuthRepository().legal
        vm.loadLegal()
        assertTrue(vm.state.value.legal is LegalState.Loaded)
    }

    @Test fun `cadastro exige e-mail valido senha e aceite dos termos`() {
        val vm = vm()
        vm.submit()
        val s = vm.state.value
        assertEquals(FormIssue.REQUIRED, s.emailIssue)
        assertEquals(FormIssue.REQUIRED, s.passwordIssue)
        assertTrue(s.termsMissing)
        assertTrue(auth.registerCalls.isEmpty())

        vm.onEmail("sem-arroba")
        vm.onPassword("abc")
        vm.onAcceptTerms(true)
        vm.submit()
        assertEquals(FormIssue.INVALID_EMAIL, vm.state.value.emailIssue)
        assertTrue(auth.registerCalls.isEmpty())
    }

    @Test fun `consentimentos opcionais nascem desmarcados`() {
        val s = vm().state.value
        assertFalse(s.acceptTerms)
        assertFalse(s.acceptAnalytics)
        assertFalse(s.acceptMarketing)
    }

    @Test fun `cadastro valido envia termos e politica com as versoes vigentes e navega para a confirmacao`() = runTest {
        val vm = vm()
        vm.onEmail("ana@example.org"); vm.onPassword("s3nha-longa"); vm.onAcceptTerms(true)
        vm.events.test {
            vm.submit()
            assertEquals(AuthEvent.VerificationRequired("ana@example.org", 45), awaitItem())
        }
        val sent = auth.registerCalls.single()
        assertEquals(
            listOf(ConsentPurpose.TERMS_OF_USE to "1.0.0", ConsentPurpose.PRIVACY_POLICY to "1.1.0"),
            sent.map { it.purpose to it.documentVersion },
        )
        assertFalse(vm.state.value.submitting)
    }

    @Test fun `opcional marcado so e enviado se existir documento vigente`() = runTest {
        val vm = vm()
        vm.onEmail("a@b.co"); vm.onPassword("x"); vm.onAcceptTerms(true)
        vm.onAcceptAnalytics(true)      // há documento
        vm.onAcceptMarketing(true)      // não há documento na lista
        vm.submit()
        assertEquals(
            listOf(ConsentPurpose.TERMS_OF_USE, ConsentPurpose.PRIVACY_POLICY, ConsentPurpose.ANALYTICS_PRODUCT),
            auth.registerCalls.single().map { it.purpose },
        )
    }

    @Test fun `erro de politica de senha do servidor aparece no campo`() = runTest {
        auth.registerResult = Outcome.Failure(
            AppError.Api(400, "VALIDATION_FAILED", fieldErrors = listOf(app.nina.domain.model.FieldError("password", "PASSWORD_POLICY"))),
        )
        val vm = vm()
        vm.onEmail("a@b.co"); vm.onPassword("123"); vm.onAcceptTerms(true)
        vm.submit()
        assertEquals(FormIssue.PASSWORD_POLICY, vm.state.value.passwordIssue)
        assertEquals("VALIDATION_FAILED", (vm.state.value.error as AppError.Api).code)
    }

    @Test fun `login com credencial invalida mostra erro generico sem navegar`() = runTest {
        auth.loginResult = Outcome.Failure(AppError.Api(401, "INVALID_CREDENTIALS"))
        val vm = vm()
        vm.setMode(AuthMode.LOGIN)
        vm.onEmail("ana@example.org"); vm.onPassword("errada")
        vm.submit()
        assertEquals(1, auth.loginCalls)
        assertEquals(R.string.error_invalid_credentials, vm.state.value.error!!.messageRes())
        assertFalse(vm.state.value.submitting)
    }

    @Test fun `login nao exige aceite de termos`() {
        val vm = vm()
        vm.setMode(AuthMode.LOGIN)
        vm.onEmail("ana@example.org"); vm.onPassword("x")
        vm.submit()
        assertEquals(1, auth.loginCalls)
        assertFalse(vm.state.value.termsMissing)
    }

    @Test fun `botao Google chama o provedor e stub indisponivel mostra mensagem sem chamar a API`() {
        val vm = vm()
        vm.onSocial(IdentityProvider.GOOGLE)
        assertEquals(listOf(IdentityProvider.GOOGLE), social.requested)
        assertEquals(AppError.SocialUnavailable, vm.state.value.error)
        assertTrue(auth.socialCalls.isEmpty())
        assertFalse(vm.state.value.submitting)
    }

    @Test fun `credencial social valida vai ao servidor com consentimentos quando os termos foram aceitos`() {
        val cred = SocialCredential(IdentityProvider.APPLE, "tok", "nonce-fixo")
        social.result = SocialAuthResult.Success(cred)
        val vm = vm()
        vm.onAcceptTerms(true)
        vm.onSocial(IdentityProvider.APPLE)
        val (sentCred, consents) = auth.socialCalls.single()
        assertEquals(cred, sentCred)
        assertEquals(2, consents.size)
    }

    @Test fun `social sem aceite nao envia consentimentos e CONSENT_REQUIRED marca os termos`() {
        social.result = SocialAuthResult.Success(SocialCredential(IdentityProvider.GOOGLE, "tok", "n"))
        auth.socialResult = Outcome.Failure(AppError.Api(403, "CONSENT_REQUIRED"))
        val vm = vm()
        vm.onSocial(IdentityProvider.GOOGLE)
        assertTrue(auth.socialCalls.single().second.isEmpty())
        assertTrue(vm.state.value.termsMissing)
        assertEquals(R.string.error_consent_required, vm.state.value.error!!.messageRes())
    }

    @Test fun `cancelamento do provedor nao mostra erro`() {
        social.result = SocialAuthResult.Cancelled
        val vm = vm()
        vm.onSocial(IdentityProvider.GOOGLE)
        assertNull(vm.state.value.error)
        assertFalse(vm.state.value.submitting)
    }
}
