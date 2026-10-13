import XCTest
@testable import NinaCore

@MainActor
final class SessionLocalDataTests: XCTestCase {
    private func queuedStore() async throws -> (InMemoryTrackingStore, DefaultEventRepository) {
        let store = InMemoryTrackingStore()
        let repo = TrackingFixtures.makeRepository(store: store)
        _ = try await repo.create(.diaper(occurredAt: TrackingFixtures.now, type: .wet, notes: nil),
                                  context: TrackingFixtures.context())
        return (store, repo)
    }

    /// RF-001-A4: sessão encerrada pelo servidor NÃO apaga a fila de mutações.
    func testSessionEndedKeepsPendingMutations() async throws {
        let (store, repo) = try await queuedStore()
        let auth = FakeAuthRepository()
        auth.stored = true
        let vm = AppSessionViewModel(auth: auth, babies: FakeBabyRepository(), preferences: InMemoryPreferenceStore(),
                                     localCleaners: [repo])
        vm.sessionEnded(.revoked)
        let mutations = await store.allMutations()
        XCTAssertEqual(mutations.count, 1)
        XCTAssertEqual(vm.route, .auth(.login))
    }

    /// Logout explícito apaga eventos e fila (aparelho compartilhado; evita enviar dados de um usuário em outra conta).
    func testExplicitLogoutClearsEventsAndQueue() async throws {
        let (store, repo) = try await queuedStore()
        let auth = FakeAuthRepository()
        auth.stored = true
        let vm = AppSessionViewModel(auth: auth, babies: FakeBabyRepository(), preferences: InMemoryPreferenceStore(),
                                     localCleaners: [repo])
        await vm.logout()
        let mutations = await store.allMutations()
        let events = await store.allEvents()
        XCTAssertTrue(mutations.isEmpty)
        XCTAssertTrue(events.isEmpty)
        XCTAssertTrue(auth.loggedOut)
    }
}
