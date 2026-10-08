package app.nina.ui

import app.nina.R
import app.nina.domain.model.AppError
import app.nina.domain.model.Outcome
import app.nina.domain.model.PendingVerification
import app.nina.domain.model.SessionState
import app.nina.testutil.FakeAuthRepository
import app.nina.testutil.MainDispatcherRule
import app.nina.ui.auth.VerifyEmailViewModel
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class VerifyEmailViewModelTest {
    @get:Rule val main = MainDispatcherRule()
    private val auth = FakeAuthRepository()

    private fun vm(resend: Int? = 3) = VerifyEmailViewModel("ana@example.org", resend, auth)

    @Test fun `codigo correto abre a sessao`() {
        val vm = vm()
        vm.onCode(" 12 34 56 ")
        assertEquals("123456", vm.state.value.code)
        vm.submit()
        assertTrue(auth.sessionState.value is SessionState.SignedIn)
        assertNull(vm.state.value.error)
        assertFalse(vm.state.value.submitting)
    }

    @Test fun `codigo vazio nao chama a API`() {
        val vm = vm()
        vm.submit()
        assertEquals("VALIDATION_FAILED", (vm.state.value.error as AppError.Api).code)
        assertTrue(auth.sessionState.value !is SessionState.SignedIn)
    }

    @Test fun `codigo invalido mantem na tela com erro`() {
        auth.verifyResult = Outcome.Failure(AppError.Api(401, "INVALID_CREDENTIALS"))
        val vm = vm()
        vm.onCode("000000")
        vm.submit()
        assertEquals(401, (vm.state.value.error as AppError.Api).httpStatus)
        assertTrue(auth.sessionState.value !is SessionState.SignedIn)
    }

    @Test fun `reenvio so apos a contagem regressiva e reinicia o relogio`() = runTest(main.dispatcher) {
        val vm = vm(resend = 3)
        assertEquals(3, vm.state.value.resendInSeconds)
        vm.resend()
        assertEquals(0, auth.resendCalls) // ainda bloqueado

        advanceTimeBy(3_100)
        assertEquals(0, vm.state.value.resendInSeconds)

        auth.resendResult = Outcome.Success(PendingVerification(10))
        vm.resend()
        assertEquals(1, auth.resendCalls)
        assertTrue(vm.state.value.resent)
        assertEquals(10, vm.state.value.resendInSeconds)
    }

    @Test fun `falha no reenvio mostra erro`() = runTest(main.dispatcher) {
        auth.resendResult = Outcome.Failure(AppError.Api(429, "RATE_LIMITED"))
        val vm = vm(resend = 1)
        advanceTimeBy(1_100)
        vm.resend()
        assertEquals(R.string.error_rate_limited, vm.state.value.error!!.messageRes())
    }
}
