package app.nina.ui

import app.nina.R
import app.cash.turbine.test
import app.nina.domain.model.AppError
import app.nina.domain.model.InvitableRole
import app.nina.domain.model.MembershipStatus
import app.nina.domain.model.Outcome
import app.nina.domain.model.Role
import app.nina.domain.model.SessionState
import app.nina.domain.model.SessionUser
import app.nina.testutil.FakeAuthRepository
import app.nina.testutil.FakeBabyRepository
import app.nina.testutil.FakeCaregiverRepository
import app.nina.testutil.MainDispatcherRule
import app.nina.testutil.testBaby
import app.nina.testutil.testMember
import app.nina.ui.caregivers.CaregiversEvent
import app.nina.ui.caregivers.CaregiversMessage
import app.nina.ui.caregivers.CaregiversViewModel
import app.nina.ui.caregivers.ConfirmAction
import app.nina.ui.invite.InviteAcceptEvent
import app.nina.ui.invite.InviteAcceptViewModel
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class CaregiversViewModelTest {
    @get:Rule val main = MainDispatcherRule()

    private val caregivers = FakeCaregiverRepository()
    private val babies = FakeBabyRepository()
    private val auth = FakeAuthRepository().also {
        it.sessionState.value = SessionState.SignedIn(SessionUser("u-1", "ana@example.org", "Ana"))
    }

    private val owner = testMember("owner", userId = "u-1", name = "Ana", role = Role.OWNER)
    private val vovo = testMember("m-2", userId = "u-2", name = "Vovó")
    private val pending = testMember("m-3", userId = null, name = null, status = MembershipStatus.PENDING, email = "tio@example.org")

    private fun vm(myRole: Role = Role.OWNER, members: List<app.nina.domain.model.Membership> = listOf(owner, vovo, pending)): CaregiversViewModel {
        babies.babies.value = listOf(testBaby(role = myRole))
        caregivers.members.value = members
        return CaregiversViewModel("b-1", caregivers, babies, auth)
    }

    @Test fun `carrega vinculos e papel do usuario no bebe`() {
        val vm = vm()
        val s = vm.state.value
        assertEquals(3, s.members.size)
        assertTrue(s.isOwner)
        assertTrue(s.isMe(owner))
        assertFalse(s.isMe(vovo))
        assertFalse(s.loading)
    }

    @Test fun `lista visivel inclui apenas ativos e pendentes`() {
        val revoked = testMember("m-4", status = MembershipStatus.REVOKED)
        val unknown = testMember("m-5", status = MembershipStatus.UNRECOGNIZED)
        val visible = CaregiversViewModel.visible(listOf(owner, vovo, pending, revoked, unknown))
        assertEquals(listOf("owner", "m-2", "m-3"), visible.map { it.id })
    }

    @Test fun `convite valida e-mail antes de enviar`() {
        val vm = vm()
        vm.openInvite()
        vm.sendInvite()
        assertEquals(FormIssue.REQUIRED, vm.state.value.invite!!.emailIssue)
        vm.onInviteEmail("invalido")
        vm.sendInvite()
        assertEquals(FormIssue.INVALID_EMAIL, vm.state.value.invite!!.emailIssue)
        assertTrue(caregivers.invites.isEmpty())
    }

    @Test fun `convite enviado fecha o dialogo e avisa`() = runTest {
        val vm = vm()
        vm.openInvite()
        vm.onInviteEmail("avo@example.org")
        vm.onInviteRole(InvitableRole.READ_ONLY)
        vm.events.test {
            vm.sendInvite()
            assertEquals(CaregiversEvent.Message(CaregiversMessage.INVITE_SENT), awaitItem())
        }
        assertEquals(listOf("avo@example.org" to InvitableRole.READ_ONLY), caregivers.invites)
        assertNull(vm.state.value.invite)
    }

    @Test fun `ALREADY_MEMBER mantem o dialogo aberto com a mensagem`() {
        caregivers.inviteResult = Outcome.Failure(AppError.Api(409, "ALREADY_MEMBER"))
        val vm = vm()
        vm.openInvite()
        vm.onInviteEmail("avo@example.org")
        vm.sendInvite()
        assertNotNull(vm.state.value.invite)
        assertFalse(vm.state.value.invite!!.sending)
        assertEquals(R.string.error_already_member, vm.state.value.error!!.messageRes())
    }

    @Test fun `remover exige confirmacao`() = runTest {
        val vm = vm()
        vm.ask(ConfirmAction.Remove(vovo))
        assertTrue(caregivers.removed.isEmpty())
        vm.events.test {
            vm.confirm()
            assertEquals(CaregiversEvent.Message(CaregiversMessage.REMOVED), awaitItem())
        }
        assertEquals(listOf("m-2"), caregivers.removed)
        assertNull(vm.state.value.confirm)
    }

    @Test fun `cancelar a confirmacao nao remove`() {
        val vm = vm()
        vm.ask(ConfirmAction.Remove(vovo))
        vm.dismissConfirm()
        vm.confirm()
        assertTrue(caregivers.removed.isEmpty())
    }

    @Test fun `reenviar e mudar papel chamam o repositorio`() = runTest {
        val vm = vm()
        vm.events.test {
            vm.resend(pending)
            assertEquals(CaregiversEvent.Message(CaregiversMessage.INVITE_RESENT), awaitItem())
            vm.changeRole(vovo, InvitableRole.READ_ONLY)
            assertEquals(CaregiversEvent.Message(CaregiversMessage.ROLE_CHANGED), awaitItem())
        }
        assertEquals(listOf("m-3"), caregivers.resent)
        assertEquals(listOf("m-2" to InvitableRole.READ_ONLY), caregivers.roleChanges)
    }

    @Test fun `mudar para o mesmo papel nao chama a API`() {
        vm().changeRole(vovo, InvitableRole.CAREGIVER)
        assertTrue(caregivers.roleChanges.isEmpty())
    }

    @Test fun `403 ao remover e exibido pelo code`() {
        caregivers.removeResult = Outcome.Failure(AppError.Api(403, "FORBIDDEN_ROLE"))
        val vm = vm()
        vm.ask(ConfirmAction.Remove(vovo))
        vm.confirm()
        assertEquals(R.string.error_forbidden_role, vm.state.value.error!!.messageRes())
    }

    @Test fun `nao titular nao e dono e pode sair do bebe`() = runTest {
        val me = testMember("m-me", userId = "u-1", name = "Ana", role = Role.CAREGIVER)
        val vm = vm(myRole = Role.CAREGIVER, members = listOf(owner.copy(userId = "u-9"), me))
        assertFalse(vm.state.value.isOwner)
        vm.ask(ConfirmAction.Leave(me))
        vm.events.test {
            vm.confirm()
            assertEquals(CaregiversEvent.LeftBaby, awaitItem())
        }
        assertEquals(listOf("m-me"), caregivers.removed)
        assertEquals(1, babies.refreshBabiesCalls)
    }

    @Test fun `vinculo revogado ao atualizar volta para a lista`() = runTest {
        caregivers.refreshResult = Outcome.Failure(AppError.Api(403, "ACCESS_REVOKED"))
        babies.babies.value = listOf(testBaby())
        val vm = CaregiversViewModel("b-1", caregivers, babies, auth)
        vm.events.test { assertEquals(CaregiversEvent.LeftBaby, awaitItem()) }
    }

    // ---- aceitar convite
    @Test fun `extrai token de link ou texto puro`() {
        assertEquals("abc123", InviteAcceptViewModel.extractToken("  abc123 "))
        assertEquals("abc123", InviteAcceptViewModel.extractToken("https://nina.invalid/invite?token=abc123&utm=x"))
        assertEquals("abc123", InviteAcceptViewModel.extractToken("nina://invite?token=abc123"))
        assertEquals("", InviteAcceptViewModel.extractToken("   "))
    }

    @Test fun `convidado ve previa e aceita`() = runTest {
        val vm = InviteAcceptViewModel(caregivers, babies)
        vm.inspect()
        assertEquals(FormIssue.REQUIRED, vm.state.value.inputIssue)
        vm.onInput("https://nina.invalid/i?token=tok-9")
        vm.inspect()
        assertEquals("tok-9", caregivers.inspectedTokens.single())
        assertEquals("Ana", vm.state.value.preview!!.inviterDisplayName)
        vm.events.test {
            vm.accept()
            assertEquals(InviteAcceptEvent.Accepted, awaitItem())
        }
        assertEquals(listOf("tok-9"), caregivers.acceptedTokens)
        assertEquals(1, babies.refreshBabiesCalls)
    }

    @Test fun `convite invalido mostra nao encontrado`() {
        caregivers.inspectResult = Outcome.Failure(AppError.Api(404, "NOT_FOUND"))
        val vm = InviteAcceptViewModel(caregivers, babies)
        vm.onInput("zzz")
        vm.inspect()
        assertNull(vm.state.value.preview)
        assertEquals(R.string.error_not_found, vm.state.value.error!!.messageRes())
    }

    @Test fun `recusar so depois de ver a previa`() = runTest {
        val vm = InviteAcceptViewModel(caregivers, babies)
        vm.onInput("tok")
        vm.decline() // sem prévia: ignorado
        vm.inspect()
        vm.events.test {
            vm.decline()
            assertEquals(InviteAcceptEvent.Declined, awaitItem())
        }
    }
}
