import XCTest
@testable import NinaCore

final class MutationQueueTests: XCTestCase {
    private let now = TrackingFixtures.now
    private let babyId = TrackingFixtures.babyId
    private let device = TrackingFixtures.deviceId

    private func mutation(_ op: MutationOp = .create, entity: UUID = UUID(), id: UUID = UUID(),
                          type: SyncEntityType = .diaperEvent, base: Int = 0, dependsOn: UUID? = nil,
                          notBefore: Date? = nil) -> QueuedMutation {
        QueuedMutation(mutationId: id, op: op, entityType: type, entityId: entity, babyId: babyId, baseVersion: base,
                       clientCreatedAt: now, deviceId: device, data: op == .delete ? nil : JSONValue.object([:]),
                       dependsOn: dependsOn, nextAttemptAt: notBefore)
    }

    // MARK: Idempotência de UUID

    func testEnqueueIsIdempotentByMutationId() {
        var queue = MutationQueueState()
        let id = UUID()
        let entity = UUID()
        XCTAssertTrue(queue.enqueue(mutation(id: id, entity: entity)))
        XCTAssertFalse(queue.enqueue(mutation(id: id, entity: entity)), "mesmo mutation_id não duplica")
        XCTAssertFalse(queue.enqueue(mutation(.update, entity: entity, id: id)), "nem com conteúdo diferente")
        XCTAssertEqual(queue.mutations.count, 1)
        XCTAssertEqual(queue.mutations[0].op, .create, "a primeira gravação vence")
    }

    func testMutationIdIsPreservedAcrossRetriesAndRecovery() {
        var queue = MutationQueueState()
        let id = UUID()
        queue.enqueue(mutation(id: id))
        queue.markInFlight([id])
        queue.markFailed(id, retryable: true, code: "TRANSIENT", now: now)
        queue.markInFlight([id])
        queue.recoverInFlight()
        XCTAssertEqual(queue.mutations.map(\.mutationId), [id])
        XCTAssertEqual(queue.mutations[0].state, .pending)
    }

    // MARK: Ordem

    func testSequenceIsStrictlyIncreasingAndBatchKeepsInsertionOrder() {
        var queue = MutationQueueState()
        let ids = (0..<5).map { _ in UUID() }
        for id in ids { queue.enqueue(mutation(id: id)) }
        XCTAssertEqual(queue.mutations.map(\.sequence), [1, 2, 3, 4, 5])
        XCTAssertEqual(queue.nextBatch(now: now).map(\.mutationId), ids)
    }

    func testBatchRespectsLimit() {
        var queue = MutationQueueState()
        for _ in 0..<150 { queue.enqueue(mutation()) }
        XCTAssertEqual(queue.nextBatch(now: now, limit: SyncWire.maxMutationsPerPush).count, 100)
    }

    func testSequenceContinuesAfterReload() {
        var queue = MutationQueueState()
        queue.enqueue(mutation())
        queue.enqueue(mutation())
        var reloaded = MutationQueueState(mutations: queue.mutations)
        reloaded.enqueue(mutation())
        XCTAssertEqual(reloaded.mutations.map(\.sequence), [1, 2, 3])
    }

    func testEntityWithEarlierBlockedMutationDoesNotJumpTheLine() {
        var queue = MutationQueueState()
        let entity = UUID()
        let other = UUID()
        let create = UUID()
        queue.enqueue(mutation(.create, entity: entity, id: create))
        queue.enqueue(mutation(.update, entity: entity, base: 1))
        queue.enqueue(mutation(.create, entity: other))
        queue.markInFlight([create])
        let batch = queue.nextBatch(now: now)
        XCTAssertEqual(batch.map(\.entityId), [other], "UPDATE espera o CREATE em voo; outra entidade segue")
    }

    func testBackoffBlocksOnlySameEntity() {
        var queue = MutationQueueState()
        let entity = UUID()
        let first = UUID()
        queue.enqueue(mutation(.create, entity: entity, id: first))
        queue.enqueue(mutation(.update, entity: entity, base: 1))
        let other = mutation(.create)
        queue.enqueue(other)
        queue.markInFlight([first])
        queue.markFailed(first, retryable: true, code: "TRANSIENT", now: now)
        XCTAssertEqual(queue.nextBatch(now: now).map(\.mutationId), [other.mutationId])
        XCTAssertEqual(queue.nextBatch(now: now.addingTimeInterval(2)).count, 3, "passado o backoff, tudo volta, em ordem")
        XCTAssertEqual(queue.nextBatch(now: now.addingTimeInterval(2)).first?.mutationId, first)
    }

    func testDependentWakeWaitsForItsSleep() {
        var queue = MutationQueueState()
        let sleep = UUID()
        let sleepCreate = UUID()
        queue.enqueue(mutation(.create, entity: sleep, id: sleepCreate, type: .sleepSession))
        queue.enqueue(mutation(.create, entity: UUID(), type: .wakeEvent, dependsOn: sleep))
        queue.markInFlight([sleepCreate])
        XCTAssertTrue(queue.nextBatch(now: now).isEmpty, "despertar não passa na frente da criação do sono")
        queue.markApplied(sleepCreate, serverVersion: 1)
        XCTAssertEqual(queue.nextBatch(now: now).count, 1)
    }

    // MARK: Retry / backoff

    func testRetryPolicyIsExponentialWithCap() {
        let policy = RetryPolicy(baseDelay: 2, multiplier: 2, maxDelay: 300)
        XCTAssertEqual(policy.delay(afterFailures: 1), 2)
        XCTAssertEqual(policy.delay(afterFailures: 2), 4)
        XCTAssertEqual(policy.delay(afterFailures: 3), 8)
        XCTAssertEqual(policy.delay(afterFailures: 20), 300, "teto de 300 s do contrato")
    }

    func testRetryableFailureBacksOffAndCountsAttempts() {
        var queue = MutationQueueState()
        let id = UUID()
        queue.enqueue(mutation(id: id))
        queue.markInFlight([id])
        queue.markFailed(id, retryable: true, code: "TRANSIENT", now: now)
        XCTAssertEqual(queue.mutation(id)?.attemptCount, 1)
        XCTAssertEqual(queue.mutation(id)?.nextAttemptAt, now.addingTimeInterval(2))
        XCTAssertTrue(queue.nextBatch(now: now.addingTimeInterval(1)).isEmpty)
        queue.markFailed(id, retryable: true, code: "TRANSIENT", now: now.addingTimeInterval(2))
        XCTAssertEqual(queue.mutation(id)?.attemptCount, 2)
        XCTAssertEqual(queue.mutation(id)?.nextAttemptAt, now.addingTimeInterval(2 + 4))
    }

    func testRetryAfterHeaderWinsOverShorterBackoffButNotOverCap() {
        var queue = MutationQueueState()
        let id = UUID()
        queue.enqueue(mutation(id: id))
        queue.markFailed(id, retryable: true, code: "RATE_LIMITED", now: now, retryAfter: 60)
        XCTAssertEqual(queue.mutation(id)?.nextAttemptAt, now.addingTimeInterval(60))
        queue.markFailed(id, retryable: true, code: "RATE_LIMITED", now: now, retryAfter: 9_999)
        XCTAssertEqual(queue.mutation(id)?.nextAttemptAt, now.addingTimeInterval(300))
    }

    func testReleaseInFlightAfterTransportFailureReturnsAllToPendingWithBackoff() {
        var queue = MutationQueueState()
        let a = UUID(), b = UUID()
        queue.enqueue(mutation(id: a))
        queue.enqueue(mutation(id: b))
        queue.markInFlight([a, b])
        queue.releaseInFlight(now: now)
        XCTAssertEqual(queue.mutations.map(\.state), [.pending, .pending])
        XCTAssertEqual(queue.mutations.map(\.attemptCount), [1, 1])
        XCTAssertTrue(queue.nextBatch(now: now).isEmpty)
        queue.retryNow()
        XCTAssertEqual(queue.nextBatch(now: now).count, 2, "'Tentar agora' zera o backoff")
    }

    func testRecoverInFlightKeepsAttemptCount() {
        var queue = MutationQueueState()
        let id = UUID()
        queue.enqueue(mutation(id: id))
        queue.markInFlight([id])
        queue.recoverInFlight()
        XCTAssertEqual(queue.mutation(id)?.state, .pending)
        XCTAssertEqual(queue.mutation(id)?.attemptCount, 0)
        XCTAssertEqual(queue.nextBatch(now: now).count, 1)
    }

    func testNonRetryableRejectionStaysVisibleAndBlocksItsEntity() {
        var queue = MutationQueueState()
        let entity = UUID()
        let create = UUID()
        queue.enqueue(mutation(.create, entity: entity, id: create))
        queue.enqueue(mutation(.update, entity: entity, base: 1))
        queue.markFailed(create, retryable: false, code: "VALIDATION_FAILED", now: now)
        XCTAssertEqual(queue.rejected().map(\.mutationId), [create])
        XCTAssertEqual(queue.pendingCount(), 1, "recusada não conta como 'aguardando envio'")
        XCTAssertTrue(queue.nextBatch(now: now).isEmpty)
        queue.requeueRejected(create)
        XCTAssertEqual(queue.nextBatch(now: now).count, 2)
    }

    // MARK: Confirmação

    func testMarkAppliedRemovesAndRebasesFollowingMutations() {
        var queue = MutationQueueState()
        let entity = UUID()
        let create = UUID()
        queue.enqueue(mutation(.create, entity: entity, id: create))
        queue.enqueue(mutation(.update, entity: entity, base: 1))
        queue.enqueue(mutation(.update, entity: entity, base: 2))
        queue.markApplied(create, serverVersion: 5)
        XCTAssertEqual(queue.mutations.map(\.baseVersion), [5, 6], "servidor já estava na versão 5 (ex.: DUPLICATE)")
        XCTAssertEqual(queue.mutations.map(\.op), [.update, .update])
    }

    func testDiscardUnsentEntityRemovesCreateUpdatesAndDependents() {
        var queue = MutationQueueState()
        let sleep = UUID()
        queue.enqueue(mutation(.create, entity: sleep, type: .sleepSession))
        queue.enqueue(mutation(.update, entity: sleep, type: .sleepSession, base: 1))
        queue.enqueue(mutation(.create, entity: UUID(), type: .wakeEvent, dependsOn: sleep))
        let keep = mutation(.create)
        queue.enqueue(keep)
        XCTAssertTrue(queue.discardUnsentEntity(sleep))
        XCTAssertEqual(queue.mutations.map(\.mutationId), [keep.mutationId])
    }

    func testDiscardUnsentRefusesWhenCreateAlreadyInFlight() {
        var queue = MutationQueueState()
        let entity = UUID()
        let create = UUID()
        queue.enqueue(mutation(.create, entity: entity, id: create))
        queue.markInFlight([create])
        XCTAssertFalse(queue.discardUnsentEntity(entity))
        XCTAssertEqual(queue.mutations.count, 1)
    }

    func testCancelPendingDeleteOnlyBeforeSend() {
        var queue = MutationQueueState()
        let entity = UUID()
        let delete = UUID()
        queue.enqueue(mutation(.delete, entity: entity, id: delete, base: 1))
        XCTAssertTrue(queue.cancelPendingDelete(forEntity: entity))
        queue.enqueue(mutation(.delete, entity: entity, base: 1))
        queue.markInFlight([queue.mutations[0].mutationId])
        XCTAssertFalse(queue.cancelPendingDelete(forEntity: entity))
    }

    func testDeleteHeldDuringUndoWindow() {
        var queue = MutationQueueState()
        queue.enqueue(mutation(.delete, base: 1, notBefore: now.addingTimeInterval(8)))
        XCTAssertTrue(queue.nextBatch(now: now.addingTimeInterval(7)).isEmpty)
        XCTAssertEqual(queue.nextBatch(now: now.addingTimeInterval(8)).count, 1)
    }

    func testRemoveAllForBaby() {
        var queue = MutationQueueState()
        queue.enqueue(mutation())
        var foreign = mutation()
        foreign.babyId = UUID()
        queue.enqueue(foreign)
        queue.removeAll(forBaby: babyId)
        XCTAssertEqual(queue.mutations.map(\.babyId), [foreign.babyId])
    }
}
