import XCTest
import SwiftData
import NinaCore
@testable import Nina

/// Mesmos cenários de offline-first do `InMemoryTrackingStore` (NinaCore), agora no SwiftData em memória.
final class SwiftDataTrackingStoreTests: XCTestCase {
    private let babyId = UUID(uuidString: "7D0A5C9E-1C0B-4D3A-B5F4-2F9D0E6A8B21")!
    private let deviceId = UUID(uuidString: "6F1D1C5E-4A3B-4F0E-9A52-0B5B3C8A7E11")!
    private let now = Date(timeIntervalSince1970: 1_791_471_600)
    private var container: ModelContainer!

    override func setUp() {
        super.setUp()
        container = LocalStore.makeContainer(inMemory: true)
    }

    private func makeStore() -> SwiftDataTrackingStore { SwiftDataTrackingStore(modelContainer: container) }

    private func repository(_ store: TrackingStore, role: Role = .owner) -> (DefaultEventRepository, TrackingContext) {
        let fixed = now
        let repository = DefaultEventRepository(store: store, deviceId: deviceId, now: { fixed })
        return (repository, TrackingContext(babyId: babyId, timeZone: "America/Sao_Paulo", role: role))
    }

    func testCreatePersistsEventAndMutationAcrossStoreInstances() async throws {
        let (repo, ctx) = repository(makeStore())
        let event = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: ctx)

        // "Reiniciar o app": outro actor sobre o mesmo banco.
        let reopened = makeStore()
        let loaded = try await reopened.events(babyId: babyId, from: nil, to: nil)
        XCTAssertEqual(loaded.map(\.id), [event.id])
        XCTAssertEqual(loaded.first?.syncStatus, .pending)
        let batch = try await reopened.nextBatch(now: now, limit: 100, babyId: nil)
        XCTAssertEqual(batch.count, 1)
        XCTAssertEqual(batch.first?.entityId, event.id)
        XCTAssertEqual(batch.first?.data?["diaper_type"]?.stringValue, "WET")
    }

    func testQueueKeepsInsertionOrderAndSequenceAfterReopen() async throws {
        let store = makeStore()
        let (repo, ctx) = repository(store)
        var ids: [UUID] = []
        for index in 0..<4 {
            let event = try await repo.create(.diaper(occurredAt: now.addingTimeInterval(Double(-index) * 60), type: .wet, notes: nil),
                                              context: ctx)
            ids.append(event.id)
        }
        let batch = try await makeStore().nextBatch(now: now, limit: 100, babyId: nil)
        XCTAssertEqual(batch.map(\.entityId), ids)
        XCTAssertEqual(batch.map(\.sequence), [1, 2, 3, 4])
    }

    func testMutationIdIsIdempotentInStorage() async throws {
        let store = makeStore()
        let event = TrackedEventFixture.diaper(babyId: babyId, at: now)
        let mutation = MutationFactory.create(for: event, mutationId: UUID(), deviceId: deviceId, now: now)
        try await store.create(event, mutation: mutation)
        try await store.create(event, mutation: mutation)
        let pending = try await store.pendingCount(babyId: nil)
        XCTAssertEqual(pending, 1)
        let events = try await store.events(babyId: babyId, from: nil, to: nil)
        XCTAssertEqual(events.count, 1)
    }

    func testOpenSleepAndWakeEventsQueries() async throws {
        let store = makeStore()
        let (repo, ctx) = repository(store)
        let open = try await repo.create(.sleep(type: .nap, start: now.addingTimeInterval(-600), end: nil, source: .timer,
                                                methodOrPlace: nil, notes: nil), context: ctx)
        let found = try await store.openSleep(babyId: babyId)
        XCTAssertEqual(found?.id, open.id)
        var stop = EventChanges()
        stop.endAt = .set(now)
        _ = try await repo.update(eventId: open.id, changes: stop, context: ctx)
        let none = try await store.openSleep(babyId: babyId)
        XCTAssertNil(none)

        let wake = try await repo.create(.wake(sleepSessionId: open.id, start: now.addingTimeInterval(-500),
                                               end: now.addingTimeInterval(-400), source: .manual), context: ctx)
        let wakes = try await store.wakeEvents(sleepSessionId: open.id)
        XCTAssertEqual(wakes.map(\.id), [wake.id])
    }

    func testDeleteTombstoneUndoAndDiscard() async throws {
        let store = makeStore()
        let synced = TrackedEventFixture.diaper(babyId: babyId, at: now, version: 3)
        let seedMutation = MutationFactory.create(for: synced, mutationId: UUID(), deviceId: deviceId, now: now)
        try await store.create(synced, mutation: seedMutation)
        // Simula a confirmação do servidor (criação aplicada).
        try await store.markInFlight([seedMutation.mutationId])
        try await store.apply([PushItemResult(mutationId: seedMutation.mutationId, status: .applied, version: 3)],
                              now: now, policy: RetryPolicy())

        let (repo, ctx) = repository(store)
        let outcome = try await repo.delete(eventId: synced.id, context: ctx)
        XCTAssertEqual(outcome, .tombstoned)
        var visible = try await store.events(babyId: babyId, from: nil, to: nil)
        XCTAssertTrue(visible.isEmpty)
        let raw = try await store.event(id: synced.id)
        XCTAssertNotNil(raw?.deletedAt)

        try await repo.undoDelete(eventId: synced.id)
        visible = try await store.events(babyId: babyId, from: nil, to: nil)
        XCTAssertEqual(visible.map(\.id), [synced.id])

        // Criação ainda não enviada: excluir descarta tudo.
        let fresh = try await repo.create(.diaper(occurredAt: now, type: .dirty, notes: nil), context: ctx)
        let discarded = try await repo.delete(eventId: fresh.id, context: ctx)
        XCTAssertEqual(discarded, .discarded)
        let gone = try await store.event(id: fresh.id)
        XCTAssertNil(gone)
        let pending = try await store.pendingCount(babyId: nil)
        XCTAssertEqual(pending, 0)
    }

    func testPushResultsUpdateVersionAndRemoveConfirmedMutations() async throws {
        let store = makeStore()
        let (repo, ctx) = repository(store)
        let event = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: ctx)
        var change = EventChanges()
        change.diaperType = .mixed
        _ = try await repo.update(eventId: event.id, changes: change, context: ctx)
        let queued = try await store.nextBatch(now: now, limit: 100, babyId: nil)
        XCTAssertEqual(queued.map(\.baseVersion), [0, 1])
        try await store.markInFlight(queued.map(\.mutationId))
        try await store.apply([PushItemResult(mutationId: queued[0].mutationId, status: .applied, version: 1),
                               PushItemResult(mutationId: queued[1].mutationId, status: .duplicate, version: 2)],
                              now: now, policy: RetryPolicy())
        let stored = try await store.event(id: event.id)
        XCTAssertEqual(stored?.version, 2)
        XCTAssertEqual(stored?.syncStatus, .synced)
        let pending = try await store.pendingCount(babyId: nil)
        XCTAssertEqual(pending, 0)
    }

    func testRetryableFailureBacksOffAndRecoverInFlight() async throws {
        let store = makeStore()
        let (repo, ctx) = repository(store)
        _ = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: ctx)
        let first = try await store.nextBatch(now: now, limit: 100, babyId: nil)
        try await store.markInFlight(first.map(\.mutationId))
        let during = try await store.nextBatch(now: now, limit: 100, babyId: nil)
        XCTAssertTrue(during.isEmpty)
        // Queda do processo: volta para pending sem penalidade.
        try await makeStore().recoverInFlight()
        let again = try await store.nextBatch(now: now, limit: 100, babyId: nil)
        XCTAssertEqual(again.map(\.mutationId), first.map(\.mutationId))

        try await store.markInFlight(again.map(\.mutationId))
        try await store.releaseInFlight(now: now, policy: RetryPolicy(), retryAfter: nil)
        let blocked = try await store.nextBatch(now: now, limit: 100, babyId: nil)
        XCTAssertTrue(blocked.isEmpty)
        let later = try await store.nextBatch(now: now.addingTimeInterval(3), limit: 100, babyId: nil)
        XCTAssertEqual(later.count, 1)
        XCTAssertEqual(later.first?.attemptCount, 1)
    }

    func testUnknownEnumValuesSurviveStorage() async throws {
        let store = makeStore()
        var event = TrackedEventFixture.diaper(babyId: babyId, at: now)
        event.diaperType = .unknown("FUTURE_TYPE")
        let mutation = MutationFactory.create(for: event, mutationId: UUID(), deviceId: deviceId, now: now)
        try await store.create(event, mutation: mutation)
        let loaded = try await store.event(id: event.id)
        XCTAssertEqual(loaded?.diaperType, .unknown("FUTURE_TYPE"))
        let batch = try await store.nextBatch(now: now, limit: 10, babyId: nil)
        XCTAssertEqual(batch.first?.data?["diaper_type"]?.stringValue, "FUTURE_TYPE")
    }

    func testDateRangeQueryAndClear() async throws {
        let store = makeStore()
        let (repo, ctx) = repository(store)
        _ = try await repo.create(.diaper(occurredAt: now.addingTimeInterval(-86_400 * 2), type: .wet, notes: nil), context: ctx)
        _ = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: ctx)
        let recent = try await store.events(babyId: babyId, from: now.addingTimeInterval(-3600), to: nil)
        XCTAssertEqual(recent.count, 1)
        try await store.clear(babyId: nil)
        let all = try await store.events(babyId: babyId, from: nil, to: nil)
        let pending = try await store.pendingCount(babyId: nil)
        XCTAssertTrue(all.isEmpty)
        XCTAssertEqual(pending, 0)
    }
}

enum TrackedEventFixture {
    static func diaper(babyId: UUID, at date: Date, version: Int = 0) -> TrackedEvent {
        TrackedEvent(id: UUID(), babyId: babyId, kind: .diaper, version: version, tz: "America/Sao_Paulo", startAt: date,
                     diaperType: .wet, createdAt: date, updatedAt: date)
    }
}
