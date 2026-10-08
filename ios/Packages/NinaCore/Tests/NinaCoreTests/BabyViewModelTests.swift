import XCTest
@testable import NinaCore

@MainActor
final class BabyViewModelTests: XCTestCase {
    private let sp = TimeZone(identifier: "America/Sao_Paulo")!
    // 2026-10-08 15:00 UTC = 12:00 em São Paulo.
    private let now = Date(timeIntervalSince1970: 1_791_471_600)
    private var saved: [Baby] = []

    private func makeForm(mode: BabyFormViewModel.Mode, babies: FakeBabyRepository = FakeBabyRepository(),
                          consents: FakeConsentRepository = FakeConsentRepository()) -> BabyFormViewModel {
        saved = []
        let now = self.now
        return BabyFormViewModel(mode: mode, babies: babies, consents: consents, deviceTimeZone: sp,
                                 now: { now }, onSaved: { [unowned self] in self.saved.append($0) })
    }

    private func date(_ s: String) -> Date { CivilDate(string: s)!.date(in: sp) }

    func testCreateRequiresNameAndGuardianDeclaration() async {
        let babies = FakeBabyRepository()
        let vm = makeForm(mode: .create, babies: babies)
        await vm.save()
        XCTAssertEqual(vm.fieldErrors[.displayName]?.key, "validation.required")
        XCTAssertEqual(vm.fieldErrors[.guardian]?.key, "validation.guardian_required")
        XCTAssertTrue(babies.created.isEmpty)
    }

    func testBirthDateInTheFutureIsRejected() async {
        let vm = makeForm(mode: .create)
        vm.displayName = "Lia"
        vm.guardianDeclaration = true
        vm.birthDate = date("2026-10-09")
        XCTAssertFalse(vm.validate())
        XCTAssertEqual(vm.fieldErrors[.birthDate]?.key, "validation.birth_in_future")
        vm.birthDate = date("2026-10-08")
        XCTAssertTrue(vm.validate(), "nascer hoje (no fuso do bebê) é válido")
    }

    func testTodayUsesBabyTimeZoneNotUTC() {
        // 2026-10-09 01:00 UTC ainda é dia 8 em São Paulo.
        let lateUTC = Date(timeIntervalSince1970: 1_791_507_600)
        let vm = BabyFormViewModel(mode: .create, babies: FakeBabyRepository(), consents: FakeConsentRepository(),
                                   deviceTimeZone: sp, now: { lateUTC }, onSaved: { _ in })
        XCTAssertEqual(vm.today.description, "2026-10-08")
    }

    func testCreateRecordsGuardianConsentBeforeCreatingBaby() async throws {
        let babies = FakeBabyRepository()
        let consents = FakeConsentRepository()
        let vm = makeForm(mode: .create, babies: babies, consents: consents)
        vm.displayName = "  Lia "
        vm.birthDate = date("2026-05-02")
        vm.hasDueDate = true
        vm.dueDate = date("2026-05-10")
        vm.sex = .female
        vm.guardianDeclaration = true
        await vm.save()

        let consent = try XCTUnwrap(consents.recorded.first)
        XCTAssertEqual(consent.purposeKey, .childDataGuardian)
        XCTAssertEqual(consent.status, .granted)
        XCTAssertEqual(consent.documentVersion, "1.1.0", "sem documento próprio, usa a versão da Política de Privacidade")
        let (create, _) = try XCTUnwrap(babies.created.first)
        XCTAssertEqual(create.displayName, "Lia")
        XCTAssertEqual(create.birthDate.description, "2026-05-02")
        XCTAssertEqual(create.dueDate?.description, "2026-05-10")
        XCTAssertEqual(create.sex, .female)
        XCTAssertEqual(create.timezone, "America/Sao_Paulo")
        XCTAssertNotNil(create.id)
        XCTAssertEqual(saved.count, 1)
    }

    func testRetryAfterFailureReusesIdempotencyKeyAndBabyId() async throws {
        let babies = FakeBabyRepository()
        babies.createResult = .failure(APIError.network(URLError(.timedOut)))
        let vm = makeForm(mode: .create, babies: babies)
        vm.displayName = "Lia"
        vm.guardianDeclaration = true
        vm.birthDate = date("2026-05-02")
        await vm.save()
        XCTAssertEqual(vm.banner, .offline)
        XCTAssertTrue(saved.isEmpty)
        await vm.save()
        XCTAssertEqual(babies.created.count, 2)
        XCTAssertEqual(babies.created[0].1, babies.created[1].1)
        XCTAssertEqual(babies.created[0].0.id, babies.created[1].0.id)
    }

    func testCreateWithoutDueDateOmitsIt() async throws {
        let babies = FakeBabyRepository()
        let vm = makeForm(mode: .create, babies: babies)
        vm.displayName = "Lia"
        vm.guardianDeclaration = true
        vm.birthDate = date("2026-05-02")
        vm.dueDate = date("2026-05-10")
        vm.hasDueDate = false
        await vm.save()
        XCTAssertNil(babies.created.first?.0.dueDate)
    }

    func testEditSendsOnlyChangedFieldsAndClearsDueDate() async throws {
        let original = Fixtures.baby(due: CivilDate(string: "2026-01-24"))
        let babies = FakeBabyRepository()
        let vm = makeForm(mode: .edit(original), babies: babies)
        XCTAssertEqual(vm.displayName, "Nina")
        XCTAssertTrue(vm.hasDueDate)
        XCTAssertEqual(vm.selectedBirthCivilDate, original.birthDate)

        vm.displayName = "Nininha"
        vm.hasDueDate = false
        await vm.save()
        let patch = try XCTUnwrap(babies.patches.first)
        XCTAssertEqual(patch.displayName, "Nininha")
        XCTAssertEqual(patch.dueDate, .clear)
        XCTAssertNil(patch.birthDate)
        XCTAssertEqual(patch.sex, .unchanged)
        XCTAssertNil(patch.timezone)
        XCTAssertEqual(saved.count, 1)
    }

    func testEditWithoutChangesDoesNotCallServer() async {
        let babies = FakeBabyRepository()
        let vm = makeForm(mode: .edit(Fixtures.baby()), babies: babies)
        await vm.save()
        XCTAssertTrue(babies.patches.isEmpty)
        XCTAssertEqual(saved.count, 1)
    }

    func testOnlyOwnerCanEdit() async {
        let babies = FakeBabyRepository()
        let vm = makeForm(mode: .edit(Fixtures.baby(role: .caregiver)), babies: babies)
        XCTAssertFalse(vm.canEdit)
        vm.displayName = "Outro"
        await vm.save()
        XCTAssertEqual(vm.banner?.key, "error.forbidden_role")
        XCTAssertTrue(babies.patches.isEmpty)
    }

    func testVersionConflictIsReported() async {
        let babies = FakeBabyRepository()
        babies.updateResult = .failure(APIError.problem(Problem(code: "VERSION_CONFLICT", status: 412), httpStatus: 412))
        let vm = makeForm(mode: .edit(Fixtures.baby()), babies: babies)
        vm.displayName = "Novo"
        await vm.save()
        XCTAssertEqual(vm.banner?.key, "error.version_conflict")
        XCTAssertTrue(saved.isEmpty)
    }

    func testBabiesViewModelShowsCacheThenServer() async {
        let repo = FakeBabyRepository()
        repo.cached = [Fixtures.baby(name: "Cache")]
        repo.refreshResult = .success(BabyListResult(babies: [Fixtures.baby(name: "Servidor")], isFromCache: false))
        let vm = BabiesViewModel(repository: repo)
        await vm.load()
        XCTAssertEqual(vm.babies.first?.displayName, "Servidor")
        XCTAssertFalse(vm.isShowingCachedData)
        XCTAssertEqual(vm.state, .loaded)
    }

    func testBabiesViewModelKeepsCachedDataWhenOffline() async {
        let repo = FakeBabyRepository()
        repo.cached = [Fixtures.baby(name: "Cache")]
        repo.refreshResult = .failure(APIError.network(URLError(.notConnectedToInternet)))
        let vm = BabiesViewModel(repository: repo)
        await vm.load()
        XCTAssertEqual(vm.babies.first?.displayName, "Cache")
        XCTAssertTrue(vm.isShowingCachedData)
        XCTAssertFalse(vm.needsFirstBaby)
    }

    func testNoBabiesMeansFirstBabyFlow() async {
        let vm = BabiesViewModel(repository: FakeBabyRepository())
        await vm.load()
        XCTAssertTrue(vm.needsFirstBaby)
    }

    func testAccessRevokedClearsLocalBabyData() async {
        let repo = FakeBabyRepository()
        repo.cached = [Fixtures.baby()]
        repo.refreshResult = .failure(APIError.problem(Problem(code: "ACCESS_REVOKED", status: 403), httpStatus: 403))
        let vm = BabiesViewModel(repository: repo)
        await vm.load()
        XCTAssertTrue(vm.babies.isEmpty)
        XCTAssertTrue(repo.cleared)
        XCTAssertEqual(vm.state, .failed(UserMessage("error.access_revoked")))
    }
}
