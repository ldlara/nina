import XCTest
@testable import NinaCore

@MainActor
final class CaregiversViewModelTests: XCTestCase {
    private let me = Fixtures.userId

    private func makeVM(role: Role, repo: FakeCaregiverRepository, onLeft: @escaping @MainActor () -> Void = {}) -> CaregiversViewModel {
        CaregiversViewModel(baby: Fixtures.baby(role: role), currentUserId: me, repository: repo, onLeftBaby: onLeft)
    }

    func testListSeparatesActiveAndPendingWithOwnerFirst() async {
        let repo = FakeCaregiverRepository()
        repo.list = .success([
            Fixtures.membership(role: .readOnly, status: .active, userId: UUID(), name: "Vovó"),
            Fixtures.membership(role: .owner, status: .active, userId: me, name: "Ana"),
            Fixtures.membership(role: .caregiver, status: .pending),
            Fixtures.membership(role: .caregiver, status: .revoked, userId: UUID())
        ])
        let vm = makeVM(role: .owner, repo: repo)
        await vm.load()
        XCTAssertEqual(vm.state, .loaded)
        XCTAssertEqual(vm.activeMembers.map(\.role), [.owner, .readOnly])
        XCTAssertEqual(vm.pendingInvitations.count, 1)
    }

    func testPermissionsByRole() {
        let repo = FakeCaregiverRepository()
        let other = Fixtures.membership(role: .caregiver, status: .active, userId: UUID())
        let mine = Fixtures.membership(role: .caregiver, status: .active, userId: me)
        let owner = Fixtures.membership(role: .owner, status: .active, userId: UUID())

        let ownerVM = makeVM(role: .owner, repo: repo)
        XCTAssertTrue(ownerVM.canManage)
        XCTAssertTrue(ownerVM.canRemove(other))
        XCTAssertFalse(ownerVM.canRemove(owner), "Owner precisa transferir a propriedade antes de sair")
        XCTAssertTrue(ownerVM.canChangeRole(other))

        let caregiverVM = makeVM(role: .caregiver, repo: repo)
        XCTAssertFalse(caregiverVM.canManage)
        XCTAssertFalse(caregiverVM.canRemove(other))
        XCTAssertTrue(caregiverVM.canRemove(mine), "pode sair do bebê (próprio vínculo)")
        XCTAssertFalse(caregiverVM.canChangeRole(mine))

        let unknownVM = makeVM(role: .unknown("FUTURE"), repo: repo)
        XCTAssertFalse(unknownVM.canManage)
    }

    func testInviteValidatesEmailAndUsesStableKeyUntilSuccess() async {
        let repo = FakeCaregiverRepository()
        let vm = makeVM(role: .owner, repo: repo)
        vm.inviteEmail = "invalido"
        let invalid = await vm.invite()
        XCTAssertFalse(invalid)
        XCTAssertEqual(vm.inviteFieldError?.key, "validation.email_invalid")
        XCTAssertTrue(repo.inviteKeys.isEmpty)

        repo.inviteResult = .failure(APIError.network(URLError(.timedOut)))
        vm.inviteEmail = "avo@example.org"
        _ = await vm.invite()
        _ = await vm.invite()
        XCTAssertEqual(repo.inviteKeys.count, 2)
        XCTAssertEqual(repo.inviteKeys[0], repo.inviteKeys[1], "retry reutiliza a Idempotency-Key")

        repo.inviteResult = nil
        let ok = await vm.invite()
        XCTAssertTrue(ok)
        XCTAssertEqual(vm.pendingInvitations.count, 1)
        XCTAssertEqual(vm.inviteEmail, "")
        _ = await vm.invite() // e-mail vazio -> inválido, sem nova chamada
        XCTAssertEqual(repo.inviteKeys.count, 3)
    }

    func testInviteAlreadyMemberShowsMappedMessage() async {
        let repo = FakeCaregiverRepository()
        repo.inviteResult = .failure(APIError.problem(Problem(code: "ALREADY_MEMBER", status: 409), httpStatus: 409))
        let vm = makeVM(role: .owner, repo: repo)
        vm.inviteEmail = "avo@example.org"
        let ok = await vm.invite()
        XCTAssertFalse(ok)
        XCTAssertEqual(vm.banner?.key, "error.already_member")
    }

    func testNonOwnerCannotInvite() async {
        let repo = FakeCaregiverRepository()
        let vm = makeVM(role: .caregiver, repo: repo)
        vm.inviteEmail = "avo@example.org"
        let ok = await vm.invite()
        XCTAssertFalse(ok)
        XCTAssertTrue(repo.inviteKeys.isEmpty)
    }

    func testOwnerRemovesCaregiver() async {
        let repo = FakeCaregiverRepository()
        let target = Fixtures.membership(role: .caregiver, status: .active, userId: UUID())
        repo.list = .success([target])
        let vm = makeVM(role: .owner, repo: repo)
        await vm.load()
        await vm.remove(target)
        XCTAssertEqual(repo.removed, [target.id])
        XCTAssertTrue(vm.memberships.isEmpty)
    }

    func testCaregiverLeavingTriggersCallback() async {
        let repo = FakeCaregiverRepository()
        let mine = Fixtures.membership(role: .caregiver, status: .active, userId: me)
        repo.list = .success([mine])
        var left = false
        let vm = makeVM(role: .caregiver, repo: repo, onLeft: { left = true })
        await vm.load()
        await vm.remove(mine)
        XCTAssertTrue(left)
    }

    func testRemoveFailureKeepsListAndShowsError() async {
        let repo = FakeCaregiverRepository()
        repo.removeError = APIError.problem(Problem(code: "FORBIDDEN_ROLE", status: 403), httpStatus: 403)
        let target = Fixtures.membership(role: .caregiver, status: .active, userId: UUID())
        repo.list = .success([target])
        let vm = makeVM(role: .owner, repo: repo)
        await vm.load()
        await vm.remove(target)
        XCTAssertEqual(vm.memberships.count, 1)
        XCTAssertEqual(vm.banner?.key, "error.forbidden_role")
    }

    func testLoadFailureSetsFailedState() async {
        let repo = FakeCaregiverRepository()
        repo.list = .failure(APIError.problem(Problem(code: "ACCESS_REVOKED", status: 403), httpStatus: 403))
        let vm = makeVM(role: .caregiver, repo: repo)
        await vm.load()
        XCTAssertEqual(vm.state, .failed(UserMessage("error.access_revoked")))
    }

    // MARK: - Convidado

    func testInvitationInspectAcceptFlow() async {
        let repo = FakeCaregiverRepository()
        var accepted: [Baby] = []
        let vm = InvitationViewModel(token: " abc123 ", repository: repo, onAccepted: { accepted.append($0) })
        XCTAssertEqual(vm.trimmedToken, "abc123")
        await vm.inspect()
        XCTAssertEqual(vm.preview?.inviterDisplayName, "Ana")
        await vm.accept()
        XCTAssertEqual(accepted.count, 1)
    }

    func testInvitationNotFoundIsUniformMessage() async {
        let repo = FakeCaregiverRepository()
        repo.previewResult = .failure(APIError.problem(Problem(code: "NOT_FOUND", status: 404), httpStatus: 404))
        let vm = InvitationViewModel(token: "x", repository: repo, onAccepted: { _ in })
        await vm.inspect()
        XCTAssertNil(vm.preview)
        XCTAssertEqual(vm.message?.key, "error.invitation_invalid")
    }

    func testInvitationDeclineAndAcceptRequiresPreview() async {
        let repo = FakeCaregiverRepository()
        var accepted = 0
        let vm = InvitationViewModel(token: "x", repository: repo, onAccepted: { _ in accepted += 1 })
        await vm.accept()
        XCTAssertEqual(accepted, 0, "não aceita às cegas, sem ver a prévia")
        await vm.inspect()
        await vm.decline()
        XCTAssertTrue(repo.declined)
        XCTAssertTrue(vm.didDecline)
    }

    func testInvitationLinkParsing() {
        XCTAssertEqual(InvitationLink.token(from: URL(string: "nina://invite?token=abc")!), "abc")
        XCTAssertEqual(InvitationLink.token(from: URL(string: "https://nina.app/invite?token=xyz")!), "xyz")
        XCTAssertNil(InvitationLink.token(from: URL(string: "https://nina.app/other?token=xyz")!))
        XCTAssertNil(InvitationLink.token(from: URL(string: "nina://invite")!))
    }
}
