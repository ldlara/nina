package app.nina.ui

import app.nina.R
import app.cash.turbine.test
import app.nina.domain.model.AppError
import app.nina.domain.model.GuardianConsentStatus
import app.nina.domain.model.Outcome
import app.nina.domain.model.Role
import app.nina.domain.model.Sex
import app.nina.testutil.FakeAuthRepository
import app.nina.testutil.FakeBabyRepository
import app.nina.testutil.MainDispatcherRule
import app.nina.testutil.testBaby
import app.nina.ui.babies.BabiesViewModel
import app.nina.ui.babies.BabyFormEvent
import app.nina.ui.babies.BabyFormViewModel
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import java.time.Clock
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset

class BabyViewModelsTest {
    @get:Rule val main = MainDispatcherRule()

    private val babies = FakeBabyRepository()
    private val auth = FakeAuthRepository()
    private val today = LocalDate.of(2026, 10, 8)
    private val clock = Clock.fixed(Instant.parse("2026-10-08T12:00:00Z"), ZoneOffset.UTC)

    private fun form(id: String? = null, tz: String = "UTC") = BabyFormViewModel(id, babies, auth, tz, clock)

    // ---- lista
    @Test fun `lista carrega do cache e atualiza pela API`() {
        babies.babies.value = listOf(testBaby())
        val vm = BabiesViewModel(babies, auth)
        assertEquals(1, vm.state.value.babies.size)
        assertEquals(1, babies.refreshBabiesCalls)
        assertFalse(vm.state.value.loading)
        assertFalse(vm.state.value.offline)
    }

    @Test fun `sem rede mostra aviso offline mas mantem os dados salvos`() {
        babies.babies.value = listOf(testBaby())
        babies.refreshBabiesResult = Outcome.Failure(AppError.Network)
        val vm = BabiesViewModel(babies, auth)
        assertTrue(vm.state.value.offline)
        assertNull(vm.state.value.error)
        assertEquals(1, vm.state.value.babies.size)
    }

    @Test fun `erro do servidor aparece como erro`() {
        babies.refreshBabiesResult = Outcome.Failure(AppError.Api(503, "UNAVAILABLE"))
        val vm = BabiesViewModel(babies, auth)
        assertEquals(R.string.error_server, vm.state.value.error!!.messageRes())
    }

    @Test fun `sair da conta chama logout`() {
        BabiesViewModel(babies, auth).logout()
        assertEquals(1, auth.logoutCalls)
    }

    // ---- criação
    @Test fun `criacao valida nome data de nascimento e declaracao de responsavel`() {
        val vm = form()
        vm.save()
        val s = vm.state.value
        assertEquals(FormIssue.REQUIRED, s.nameIssue)
        assertEquals(FormIssue.REQUIRED, s.birthIssue)
        assertTrue(s.guardianMissing)
        assertTrue(babies.createdDrafts.isEmpty())
    }

    @Test fun `data de nascimento no futuro e rejeitada localmente`() {
        val vm = form()
        vm.onName("Nina"); vm.onBirthDate(today.plusDays(1)); vm.onGuardian(true)
        vm.save()
        assertEquals(FormIssue.FUTURE_DATE, vm.state.value.birthIssue)
        assertTrue(babies.createdDrafts.isEmpty())
    }

    @Test fun `nome acima de 60 caracteres e rejeitado`() {
        val vm = form()
        vm.onName("a".repeat(61)); vm.onBirthDate(today); vm.onGuardian(true)
        vm.save()
        assertEquals(FormIssue.NAME_TOO_LONG, vm.state.value.nameIssue)
    }

    @Test fun `criacao registra a declaracao antes de criar e envia nascimento e data prevista separados`() = runTest {
        val vm = form()
        vm.onName("  Nina "); vm.onBirthDate(LocalDate.of(2026, 1, 10)); vm.onDueDate(LocalDate.of(2026, 1, 24))
        vm.onSex(Sex.FEMALE); vm.onGuardian(true)
        vm.events.test {
            vm.save()
            assertEquals(BabyFormEvent.Created("b-1"), awaitItem())
        }
        assertEquals(listOf("1.0.0"), auth.grantedVersions)
        val draft = babies.createdDrafts.single()
        assertEquals("Nina", draft.displayName)
        assertEquals(LocalDate.of(2026, 1, 10), draft.birthDate)
        assertEquals(LocalDate.of(2026, 1, 24), draft.dueDate)
        assertEquals(Sex.FEMALE, draft.sex)
        assertEquals("UTC", draft.timezone)
    }

    @Test fun `declaracao ja concedida no servidor esconde a caixa e nao registra de novo`() = runTest {
        auth.guardianStatus = Outcome.Success(GuardianConsentStatus(true, null))
        val vm = form()
        assertTrue(vm.state.value.guardianGranted)
        vm.onName("Nina"); vm.onBirthDate(today)
        vm.events.test {
            vm.save()
            awaitItem()
        }
        assertTrue(auth.grantedVersions.isEmpty())
    }

    @Test fun `CONSENT_REQUIRED do servidor volta a pedir a declaracao`() {
        auth.guardianStatus = Outcome.Success(GuardianConsentStatus(true, null))
        babies.createResult = Outcome.Failure(AppError.Api(403, "CONSENT_REQUIRED"))
        val vm = form()
        vm.onName("Nina"); vm.onBirthDate(today)
        vm.save()
        val s = vm.state.value
        assertFalse(s.guardianGranted)
        assertTrue(s.guardianMissing)
        assertEquals(R.string.error_consent_required, s.error!!.messageRes())
        assertFalse(s.saving)
    }

    @Test fun `falha ao registrar a declaracao nao cria o bebe`() {
        auth.grantResult = Outcome.Failure(AppError.Network)
        val vm = form()
        vm.onName("Nina"); vm.onBirthDate(today); vm.onGuardian(true)
        vm.save()
        assertTrue(babies.createdDrafts.isEmpty())
        assertEquals(AppError.Network, vm.state.value.error)
    }

    @Test fun `erros de campo do servidor sao mapeados`() {
        auth.guardianStatus = Outcome.Success(GuardianConsentStatus(true, null))
        babies.createResult = Outcome.Failure(
            AppError.Api(400, "VALIDATION_FAILED", fieldErrors = listOf(app.nina.domain.model.FieldError("birth_date", "FUTURE_DATE"))),
        )
        val vm = form()
        vm.onName("Nina"); vm.onBirthDate(today)
        vm.save()
        assertEquals(FormIssue.FUTURE_DATE, vm.state.value.birthIssue)
    }

    // ---- edição / idade
    @Test fun `edicao mostra age_calculation vinda da API e preenche os campos`() {
        babies.babies.value = listOf(testBaby())
        val vm = form("b-1")
        val s = vm.state.value
        assertEquals("Nina", s.name)
        assertEquals(LocalDate.of(2026, 1, 24), s.dueDate)
        assertEquals(271, s.baby!!.ageCalculation!!.chronologicalDays)
        assertEquals(257, s.baby!!.ageCalculation!!.correctedDays)
        assertEquals(24, s.correctedWindowMonths)
        assertTrue(s.canEdit)
    }

    @Test fun `correcao que nao se aplica chega como nulo e nunca como zero`() {
        babies.babies.value = listOf(testBaby(dueDate = null, correctedDays = null))
        val vm = form("b-1")
        assertNull(vm.state.value.baby!!.ageCalculation!!.correctedDays)
        assertFalse(vm.state.value.baby!!.ageCalculation!!.correctionApplied)
    }

    @Test fun `somente o titular edita - caregiver ve o perfil sem salvar`() {
        babies.babies.value = listOf(testBaby(role = Role.CAREGIVER))
        val vm = form("b-1")
        assertFalse(vm.state.value.canEdit)
        vm.onName("Outro")
        vm.save()
        assertTrue(babies.updatedDrafts.isEmpty())
    }

    @Test fun `salvar edicao envia so o rascunho e atualiza versao e idade`() {
        babies.babies.value = listOf(testBaby())
        val vm = form("b-1")
        vm.onName("Nininha")
        vm.onDueDate(null)
        vm.save()
        val (current, draft) = babies.updatedDrafts.single()
        assertEquals(3, current.version)
        assertEquals("Nininha", draft.displayName)
        assertNull(draft.dueDate)
        assertTrue(vm.state.value.saved)
        assertEquals(4, vm.state.value.baby!!.version)
    }

    @Test fun `conflito de versao mostra erro e volta a refletir o servidor`() {
        babies.babies.value = listOf(testBaby())
        val vm = form("b-1")
        vm.onName("Meu nome")
        babies.updateResult = Outcome.Failure(AppError.Api(412, "VERSION_CONFLICT"))
        vm.save()
        assertEquals(R.string.error_version_conflict, vm.state.value.error!!.messageRes())
        // o repositório recarregou o servidor: nova versão chega pelo Flow e substitui os campos
        babies.babies.value = listOf(testBaby(name = "Nome do outro cuidador", version = 5))
        assertEquals("Nome do outro cuidador", vm.state.value.name)
        assertEquals(5, vm.state.value.baby!!.version)
    }

    @Test fun `403 FORBIDDEN_ROLE ao salvar e exibido pelo code`() {
        babies.babies.value = listOf(testBaby())
        val vm = form("b-1")
        vm.onName("X")
        babies.updateResult = Outcome.Failure(AppError.Api(403, "FORBIDDEN_ROLE"))
        vm.save()
        assertEquals(R.string.error_forbidden_role, vm.state.value.error!!.messageRes())
    }
}
