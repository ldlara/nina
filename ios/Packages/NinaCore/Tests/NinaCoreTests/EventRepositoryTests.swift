import XCTest
@testable import NinaCore

final class EventRepositoryTests: XCTestCase {
    private var store: InMemoryTrackingStore!
    private var clock: TestClock!
    private var repo: DefaultEventRepository!
    private let ctx = TrackingFixtures.context()

    override func setUp() {
        super.setUp()
        store = InMemoryTrackingStore()
        clock = TestClock()
        repo = TrackingFixtures.makeRepository(store: store, clock: clock)
    }

    private var now: Date { clock.now }

    // MARK: Escrita local primeiro (RF-008-A7, RF-009-A5)

    func testCreateWritesEventAndMutationTogetherAsPending() async throws {
        let event = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: "  "), context: ctx)
        XCTAssertEqual(event.syncStatus, .pending)
        XCTAssertEqual(event.version, 0)
        XCTAssertNil(event.notes, "texto em branco vira nulo")
        XCTAssertEqual(event.createdBy, TrackingFixtures.author)
        let mutations = await store.allMutations()
        XCTAssertEqual(mutations.count, 1)
        let m = mutations[0]
        XCTAssertEqual(m.op, .create)
        XCTAssertEqual(m.entityType, .diaperEvent)
        XCTAssertEqual(m.entityId, event.id)
        XCTAssertEqual(m.baseVersion, 0)
        XCTAssertEqual(m.deviceId, TrackingFixtures.deviceId)
        XCTAssertEqual(m.clientCreatedAt, now)
        XCTAssertEqual(m.data?["diaper_type"]?.stringValue, "WET")
        XCTAssertEqual(m.data?["tz"]?.stringValue, "America/Sao_Paulo")
        XCTAssertNotEqual(m.mutationId, event.id, "mutation_id é independente do id da entidade")
    }

    func testEventsSurviveReopeningTheStore() async throws {
        let first = try await repo.create(.diaper(occurredAt: now, type: .dirty, notes: nil), context: ctx)
        // "Reiniciar o app": novo repositório sobre o mesmo store.
        let reopened = TrackingFixtures.makeRepository(store: store, clock: clock)
        let loaded = try await reopened.events(babyId: ctx.babyId, from: nil, to: nil)
        XCTAssertEqual(loaded.map(\.id), [first.id])
        XCTAssertEqual(loaded.first?.syncStatus, .pending)
    }

    func testReadOnlyCannotWrite() async {
        let readOnly = TrackingFixtures.context(role: .readOnly)
        do {
            _ = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: readOnly)
            XCTFail("ReadOnly não registra (INV-13)")
        } catch {
            XCTAssertEqual(error as? EventError, .forbidden)
        }
        let unknownRole = TrackingFixtures.context(role: .unknown("FUTURE"))
        do {
            _ = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: unknownRole)
            XCTFail("papel desconhecido nunca escreve")
        } catch {
            XCTAssertEqual(error as? EventError, .forbidden)
        }
        let all = await store.allMutations()
        XCTAssertTrue(all.isEmpty)
    }

    // MARK: Timer de sono

    func testSecondOpenSleepIsRefusedAndReturnsTheExistingOne() async throws {
        let open = try await repo.create(.sleep(type: .nap, start: now, end: nil, source: .timer, methodOrPlace: nil, notes: nil),
                                         context: ctx)
        do {
            _ = try await repo.create(.sleep(type: .nap, start: now, end: nil, source: .timer, methodOrPlace: nil, notes: nil),
                                      context: ctx)
            XCTFail("INV-02")
        } catch {
            guard case .sleepAlreadyOpen(let existing)? = error as? EventError else { return XCTFail("\(error)") }
            XCTAssertEqual(existing.id, open.id)
        }
        let count = await store.allMutations().count
        XCTAssertEqual(count, 1)
    }

    func testStopSleepSendsPatchWithPredictedBaseVersionOne() async throws {
        let open = try await repo.create(.sleep(type: .nap, start: now, end: nil, source: .timer, methodOrPlace: nil, notes: nil),
                                         context: ctx)
        clock.advance(48 * 60)
        var changes = EventChanges()
        changes.endAt = .set(now)
        let stopped = try await repo.update(eventId: open.id, changes: changes, context: ctx)
        XCTAssertEqual(stopped.endAt, now)
        XCTAssertEqual(stopped.duration, 2880.0)
        let mutations = await store.allMutations()
        XCTAssertEqual(mutations.map(\.op), [.create, .update])
        XCTAssertEqual(mutations[1].baseVersion, 1, "criação pendente conta como versão 1 (exemplo do contrato)")
        XCTAssertEqual(mutations[1].data?.objectKeys, ["end_at"])
        let openAfter = try await repo.openSleep(babyId: ctx.babyId)
        XCTAssertNil(openAfter)
    }

    func testRetroactiveSleepStartIsStoredAsGivenWithTimerSource() async throws {
        let start = now.addingTimeInterval(-15 * 60)
        let event = try await repo.create(.sleep(type: .nap, start: start, end: nil, source: .timer, methodOrPlace: nil, notes: nil),
                                          context: ctx)
        XCTAssertEqual(event.startAt, start)
        XCTAssertEqual(event.sleepSource, .timer)
    }

    // MARK: Validação

    func testValidationRejectsEndBeforeStartAndFutureTimesWithoutWriting() async {
        do {
            _ = try await repo.create(.sleep(type: .nap, start: now, end: now.addingTimeInterval(-60), source: .manual,
                                             methodOrPlace: nil, notes: nil), context: ctx)
            XCTFail()
        } catch {
            guard case .validation(let issues)? = error as? EventError else { return XCTFail("\(error)") }
            XCTAssertTrue(issues.contains(FieldIssue(field: "endAt", code: "end_before_start")))
        }
        do {
            _ = try await repo.create(.diaper(occurredAt: now.addingTimeInterval(3600), type: .wet, notes: nil), context: ctx)
            XCTFail()
        } catch {
            guard case .validation(let issues)? = error as? EventError else { return XCTFail("\(error)") }
            XCTAssertTrue(issues.contains(FieldIssue(field: "startAt", code: "future_time")))
        }
        let mutations = await store.allMutations()
        XCTAssertTrue(mutations.isEmpty)
    }

    func testBreastfeedingRequiresEndAndBottleRequiresVolume() {
        let e1 = EventDraft.breastfeeding(side: .left, start: now.addingTimeInterval(-600), end: now, notes: nil)
            .build(id: UUID(), babyId: UUID(), tz: "America/Sao_Paulo", now: now, author: nil)
        XCTAssertTrue(EventValidator.validate(e1, now: now).isEmpty)
        var open = e1
        open.endAt = nil
        XCTAssertTrue(EventValidator.validate(open, now: now).contains(FieldIssue(field: "endAt", code: "end_required")))

        var bottle = EventDraft.bottle(start: now, end: nil, volumeMl: 120, milkType: .formula, notes: nil)
            .build(id: UUID(), babyId: UUID(), tz: "America/Sao_Paulo", now: now, author: nil)
        XCTAssertTrue(EventValidator.validate(bottle, now: now).isEmpty, "fim é opcional na mamadeira")
        bottle.volumeMl = nil
        XCTAssertEqual(EventValidator.validate(bottle, now: now).first, FieldIssue(field: "volumeMl", code: "volume_required"))
        bottle.volumeMl = 5001
        XCTAssertEqual(EventValidator.validate(bottle, now: now).first, FieldIssue(field: "volumeMl", code: "volume_range"))
    }

    func testMilkTypeAndSideDoNotApplyOutsideTheirKinds() {
        var breast = EventDraft.breastfeeding(side: .right, start: now.addingTimeInterval(-60), end: now, notes: nil)
            .build(id: UUID(), babyId: UUID(), tz: "America/Sao_Paulo", now: now, author: nil)
        breast.milkType = .formula
        XCTAssertTrue(EventValidator.validate(breast, now: now).contains(FieldIssue(field: "kind", code: "not_applicable")))
        XCTAssertNil(EventDraft.breastfeeding(side: .right, start: now, end: now, notes: nil)
            .build(id: UUID(), babyId: UUID(), tz: "UTC", now: now, author: nil).milkType)
    }

    // MARK: Edição

    func testEditingUnsentCreateKeepsOrderAndPredictsBaseVersions() async throws {
        let event = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: ctx)
        var one = EventChanges(); one.diaperType = .dirty
        var two = EventChanges(); two.notes = .set("cor normal")
        _ = try await repo.update(eventId: event.id, changes: one, context: ctx)
        let edited = try await repo.update(eventId: event.id, changes: two, context: ctx)
        XCTAssertEqual(edited.diaperType, .dirty)
        XCTAssertEqual(edited.notes, "cor normal")
        let mutations = await store.allMutations()
        XCTAssertEqual(mutations.map(\.baseVersion), [0, 1, 2])
        XCTAssertEqual(mutations.map(\.sequence), [1, 2, 3])
        XCTAssertEqual(mutations[1].data?["diaper_type"]?.stringValue, "DIRTY")
    }

    func testEditingChangesFeedingTypeIsImpossibleAndInapplicableFieldsAreRefused() async throws {
        let bottle = try await repo.create(.bottle(start: now, end: nil, volumeMl: 90, milkType: nil, notes: nil), context: ctx)
        var changes = EventChanges()
        changes.side = .set(.left)
        do {
            _ = try await repo.update(eventId: bottle.id, changes: changes, context: ctx)
            XCTFail("lado não se aplica a mamadeira")
        } catch {
            guard case .validation(let issues)? = error as? EventError else { return XCTFail("\(error)") }
            XCTAssertTrue(issues.contains(FieldIssue(field: "side", code: "not_applicable")))
        }
    }

    // MARK: Exclusão e tombstone

    func testDeletingUnsentCreateDiscardsEverything() async throws {
        let event = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: ctx)
        let outcome = try await repo.delete(eventId: event.id, context: ctx)
        XCTAssertEqual(outcome, .discarded)
        let mutations = await store.allMutations()
        XCTAssertTrue(mutations.isEmpty, "nada chegou ao servidor: sem DELETE")
        let all = await store.allEvents()
        XCTAssertTrue(all.isEmpty)
    }

    func testDeletingSyncedEventCreatesTombstoneAndHeldDelete() async throws {
        let synced = TrackingFixtures.event(kind: .diaper, version: 3)
        let store = InMemoryTrackingStore(events: [synced])
        let repo = TrackingFixtures.makeRepository(store: store, clock: clock)
        let outcome = try await repo.delete(eventId: synced.id, context: ctx)
        XCTAssertEqual(outcome, .tombstoned)
        let visible = try await repo.events(babyId: ctx.babyId, from: nil, to: nil)
        XCTAssertTrue(visible.isEmpty, "tombstone local some das telas")
        let stored = try await store.event(id: synced.id)
        XCTAssertNotNil(stored?.deletedAt)
        let mutations = await store.allMutations()
        XCTAssertEqual(mutations.count, 1)
        XCTAssertEqual(mutations[0].op, .delete)
        XCTAssertEqual(mutations[0].baseVersion, 3)
        XCTAssertNil(mutations[0].data)
        XCTAssertEqual(mutations[0].nextAttemptAt, now.addingTimeInterval(8), "segurado na janela de desfazer")
    }

    func testUndoDeleteRestoresEventWhileDeleteIsNotSent() async throws {
        let synced = TrackingFixtures.event(kind: .diaper, version: 3)
        let store = InMemoryTrackingStore(events: [synced])
        let repo = TrackingFixtures.makeRepository(store: store, clock: clock)
        _ = try await repo.delete(eventId: synced.id, context: ctx)
        try await repo.undoDelete(eventId: synced.id)
        let visible = try await repo.events(babyId: ctx.babyId, from: nil, to: nil)
        XCTAssertEqual(visible.map(\.id), [synced.id])
        XCTAssertEqual(visible.first?.syncStatus, .synced)
        let mutations = await store.allMutations()
        XCTAssertTrue(mutations.isEmpty)
    }

    func testUndoDeleteFailsOnceDeleteWasSent() async throws {
        let synced = TrackingFixtures.event(kind: .diaper, version: 3)
        let store = InMemoryTrackingStore(events: [synced])
        let repo = TrackingFixtures.makeRepository(store: store, clock: clock)
        _ = try await repo.delete(eventId: synced.id, context: ctx)
        let id = await store.allMutations()[0].mutationId
        try await store.markInFlight([id])
        do {
            try await repo.undoDelete(eventId: synced.id)
            XCTFail()
        } catch {
            XCTAssertEqual(error as? EventError, .cannotUndo)
        }
    }

    func testDeletingSleepHidesItsWakeEventsAndUndoBringsThemBack() async throws {
        let sleep = TrackingFixtures.event(kind: .sleep, start: now.addingTimeInterval(-3600),
                                           end: now.addingTimeInterval(-600), version: 2)
        var wake = TrackingFixtures.event(kind: .wake, start: now.addingTimeInterval(-3000), version: 1)
        wake.sleepSessionId = sleep.id
        let store = InMemoryTrackingStore(events: [sleep, wake])
        let repo = TrackingFixtures.makeRepository(store: store, clock: clock)
        _ = try await repo.delete(eventId: sleep.id, context: ctx)
        var wakes = try await repo.wakeEvents(sleepSessionId: sleep.id)
        XCTAssertTrue(wakes.isEmpty)
        try await repo.undoDelete(eventId: sleep.id)
        wakes = try await repo.wakeEvents(sleepSessionId: sleep.id)
        XCTAssertEqual(wakes.map(\.id), [wake.id])
    }

    // MARK: Despertares (WakeEvent)

    func testWakeEventMustBelongToExistingSleepAndFitInside() async throws {
        let start = now.addingTimeInterval(-3600)
        let sleep = try await repo.create(.sleep(type: .night, start: start, end: now.addingTimeInterval(-600), source: .manual,
                                                 methodOrPlace: nil, notes: nil), context: ctx)
        let wake = try await repo.create(.wake(sleepSessionId: sleep.id, start: start.addingTimeInterval(600),
                                               end: start.addingTimeInterval(900), source: .manual), context: ctx)
        XCTAssertEqual(wake.kind, .wake)
        let mutations = await store.allMutations()
        XCTAssertEqual(mutations.last?.entityType, .wakeEvent)
        XCTAssertEqual(mutations.last?.dependsOn, sleep.id)
        XCTAssertNil(mutations.last?.data?["tz"], "WakeEventData não tem tz")
        XCTAssertEqual(mutations.last?.data?["sleep_session_id"]?.stringValue, sleep.id.uuidString.lowercased())

        do {
            _ = try await repo.create(.wake(sleepSessionId: sleep.id, start: start.addingTimeInterval(-600),
                                            end: start.addingTimeInterval(60), source: .manual), context: ctx)
            XCTFail("fora da sessão")
        } catch {
            guard case .validation(let issues)? = error as? EventError else { return XCTFail("\(error)") }
            XCTAssertTrue(issues.contains(FieldIssue(field: "startAt", code: "wake_outside_sleep")))
        }
        do {
            _ = try await repo.create(.wake(sleepSessionId: UUID(), start: start, end: start.addingTimeInterval(60),
                                            source: .manual), context: ctx)
            XCTFail("sono inexistente")
        } catch {
            XCTAssertNotNil(error as? EventError)
        }
    }

    // MARK: Resultado do push (aplicado à fila)

    func testAppliedAndDuplicateConfirmEventsAndAdvanceVersion() async throws {
        let event = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: ctx)
        var change = EventChanges(); change.diaperType = .mixed
        _ = try await repo.update(eventId: event.id, changes: change, context: ctx)
        let queued = await store.allMutations()
        try await store.markInFlight(queued.map(\.mutationId))

        try await store.apply([PushItemResult(mutationId: queued[0].mutationId, status: .applied, version: 1)],
                              now: now, policy: RetryPolicy())
        var stored = try await store.event(id: event.id)
        XCTAssertEqual(stored?.version, 1)
        XCTAssertEqual(stored?.syncStatus, .pending, "ainda há o UPDATE na fila")
        // O segundo resultado é DUPLICATE (reenvio após queda): mesmo efeito de APPLIED.
        try await store.apply([PushItemResult(mutationId: queued[1].mutationId, status: .duplicate, version: 2)],
                              now: now, policy: RetryPolicy())
        stored = try await store.event(id: event.id)
        XCTAssertEqual(stored?.version, 2)
        XCTAssertEqual(stored?.syncStatus, .synced)
        let remaining = await store.allMutations()
        XCTAssertTrue(remaining.isEmpty)
    }

    func testAppliedDeleteRemovesTheRow() async throws {
        let synced = TrackingFixtures.event(kind: .diaper, version: 3)
        let store = InMemoryTrackingStore(events: [synced])
        let repo = TrackingFixtures.makeRepository(store: store, clock: clock)
        _ = try await repo.delete(eventId: synced.id, context: ctx)
        let m = await store.allMutations()[0]
        try await store.apply([PushItemResult(mutationId: m.mutationId, status: .applied, version: 4)], now: now, policy: RetryPolicy())
        let stored = try await store.event(id: synced.id)
        XCTAssertNil(stored)
    }

    func testRejectedNonRetryableMarksEventAndKeepsMutationVisible() async throws {
        let event = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: ctx)
        let m = await store.allMutations()[0]
        try await store.apply([PushItemResult(mutationId: m.mutationId, status: .rejected, retryable: false,
                                              problemCode: "VALIDATION_FAILED")], now: now, policy: RetryPolicy())
        let stored = try await store.event(id: event.id)
        XCTAssertEqual(stored?.syncStatus, .rejected)
        let status = try await repo.queueStatus(babyId: ctx.babyId)
        XCTAssertEqual(status, QueueStatus(pending: 0, rejected: 1))
        try await repo.discardRejected(babyId: ctx.babyId)
        let gone = try await store.event(id: event.id)
        XCTAssertNil(gone, "criação recusada e descartada some junto com o registro")
    }

    func testRejectedRetryableBacksOffAndStaysPending() async throws {
        _ = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: ctx)
        let m = await store.allMutations()[0]
        try await store.markInFlight([m.mutationId])
        try await store.apply([PushItemResult(mutationId: m.mutationId, status: .rejected, retryable: true,
                                              problemCode: "TRANSIENT")], now: now, policy: RetryPolicy())
        let after = await store.allMutations()[0]
        XCTAssertEqual(after.state, .pending)
        XCTAssertEqual(after.attemptCount, 1)
        let batchNow = try await store.nextBatch(now: now, limit: 100, babyId: nil)
        XCTAssertTrue(batchNow.isEmpty)
        let batchLater = try await store.nextBatch(now: now.addingTimeInterval(3), limit: 100, babyId: nil)
        XCTAssertEqual(batchLater.count, 1)
    }

    func testEntityDeletedOnServerRemovesLocalCopy() async throws {
        let synced = TrackingFixtures.event(kind: .diaper, version: 3)
        let store = InMemoryTrackingStore(events: [synced])
        let repo = TrackingFixtures.makeRepository(store: store, clock: clock)
        var change = EventChanges(); change.diaperType = .dirty
        _ = try await repo.update(eventId: synced.id, changes: change, context: ctx)
        let m = await store.allMutations()[0]
        try await store.apply([PushItemResult(mutationId: m.mutationId, status: .rejected, retryable: false,
                                              problemCode: "ENTITY_DELETED")], now: now, policy: RetryPolicy())
        let stored = try await store.event(id: synced.id)
        XCTAssertNil(stored, "exclusão vence; update sobre tombstone não ressuscita")
        let mutations = await store.allMutations()
        XCTAssertTrue(mutations.isEmpty)
    }

    func testClearLocalDataRemovesEventsAndQueue() async throws {
        _ = try await repo.create(.diaper(occurredAt: now, type: .wet, notes: nil), context: ctx)
        await repo.clearLocalData()
        let mutations = await store.allMutations()
        let events = await store.allEvents()
        XCTAssertTrue(mutations.isEmpty)
        XCTAssertTrue(events.isEmpty)
    }
}
