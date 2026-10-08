import Foundation

/// Resultado de excluir um evento localmente.
public enum DeleteOutcome: Equatable, Sendable {
    /// A criação nunca saiu do aparelho: nada foi ao servidor, o registro e suas mutações foram descartados.
    case discarded
    /// Tombstone local + mutação `DELETE` na fila (o registro some das telas; a linha é removida na confirmação).
    case tombstoned
    case notFound
}

/// Resultado de uma mutação no push, já reduzido ao que a fila precisa. A decodificação do contrato fica em
/// `PushResponse`; a rede (Onda 5) só traduz uma coisa na outra.
public struct PushItemResult: Equatable, Sendable {
    public var mutationId: UUID
    public var status: PushStatus
    public var version: Int?
    public var retryable: Bool?
    public var problemCode: String?
    public var retryAfter: TimeInterval?

    public init(mutationId: UUID, status: PushStatus, version: Int? = nil, retryable: Bool? = nil,
                problemCode: String? = nil, retryAfter: TimeInterval? = nil) {
        self.mutationId = mutationId
        self.status = status
        self.version = version
        self.retryable = retryable
        self.problemCode = problemCode
        self.retryAfter = retryAfter
    }
}

/// Estado de tracking (eventos tocados + fila) com todas as operações compostas e atômicas do offline-first.
/// É código puro e síncrono: a mesma lógica serve ao `InMemoryTrackingStore` (testes) e ao SwiftData (app),
/// que carrega só as linhas necessárias, aplica a operação e persiste a diferença (`changes(from:)`).
public struct TrackingState: Sendable {
    public var events: [UUID: TrackedEvent]
    public var queue: MutationQueueState

    public init(events: [UUID: TrackedEvent] = [:], queue: MutationQueueState = MutationQueueState()) {
        self.events = events
        self.queue = queue
    }

    // MARK: Escrita local (um evento + sua mutação, atomicamente)

    public mutating func create(_ event: TrackedEvent, mutation: QueuedMutation) {
        // Reenvio da mesma criação (mesmo mutation_id) não duplica nada.
        guard queue.enqueue(mutation) else { return }
        events[event.id] = event
    }

    public mutating func update(_ event: TrackedEvent, mutation: QueuedMutation) {
        guard queue.enqueue(mutation) else { return }
        events[event.id] = event
    }

    public mutating func delete(eventId: UUID, at date: Date, mutation: QueuedMutation) -> DeleteOutcome {
        guard var event = events[eventId] else { return .notFound }
        let create = queue.mutations(forEntity: eventId).first { $0.op == .create }
        if queue.discardUnsentEntity(eventId) || (create?.state == .rejected && event.version == 0) {
            queue.removeAll(forEntity: eventId)
            removeEventAndWakes(eventId)
            return .discarded
        }
        guard queue.enqueue(mutation) else { return .tombstoned }
        event.deletedAt = date
        event.syncStatus = .pending
        events[eventId] = event
        if event.kind == .sleep {
            // O servidor apaga os despertares em cascata; localmente eles somem junto e voltam se houver "Desfazer".
            for id in wakeIds(of: eventId) {
                events[id]?.deletedAt = date
            }
        }
        return .tombstoned
    }

    /// "Desfazer" exclusão: só vale enquanto o `DELETE` não foi enviado.
    public mutating func undoDelete(eventId: UUID) -> Bool {
        guard var event = events[eventId], event.deletedAt != nil, queue.cancelPendingDelete(forEntity: eventId) else {
            return false
        }
        event.deletedAt = nil
        event.syncStatus = queue.unconfirmedCount(forEntity: eventId) == 0 ? .synced : .pending
        events[eventId] = event
        if event.kind == .sleep {
            for id in wakeIds(of: eventId) { events[id]?.deletedAt = nil }
        }
        return true
    }

    // MARK: Resultado do push

    public mutating func apply(_ result: PushItemResult, now: Date, policy: RetryPolicy = RetryPolicy()) {
        guard let mutation = queue.mutation(result.mutationId) else { return }
        switch result.status {
        case .applied, .duplicate:
            confirm(mutation, version: result.version)
        case .rejected:
            let code = result.problemCode
            // Já excluído no servidor: o resultado desejado (sumir) já aconteceu. Idempotente.
            if code == "ENTITY_DELETED" || (code == "ENTITY_NOT_FOUND" && mutation.op == .delete) {
                queue.markApplied(mutation.mutationId, serverVersion: nil)
                queue.removeAll(forEntity: mutation.entityId)
                removeEventAndWakes(mutation.entityId)
            } else if result.retryable == true {
                queue.markFailed(mutation.mutationId, retryable: true, code: code, now: now, policy: policy,
                                 retryAfter: result.retryAfter)
            } else {
                // `retryable` ausente em REJECTED é tratado como definitivo: nunca se descarta em silêncio.
                queue.markFailed(mutation.mutationId, retryable: false, code: code, now: now, policy: policy)
                events[mutation.entityId]?.syncStatus = .rejected
            }
        case .unknown:
            // Status futuro: não presumimos sucesso. Volta para a fila com backoff.
            queue.markFailed(mutation.mutationId, retryable: true, code: "UNKNOWN_STATUS", now: now, policy: policy)
        }
    }

    private mutating func confirm(_ mutation: QueuedMutation, version: Int?) {
        queue.markApplied(mutation.mutationId, serverVersion: version)
        let entityId = mutation.entityId
        if mutation.op == .delete {
            queue.removeAll(forEntity: entityId)
            removeEventAndWakes(entityId)
            return
        }
        guard var event = events[entityId] else { return }
        if let version { event.version = max(event.version, version) }
        if event.deletedAt == nil {
            event.syncStatus = queue.unconfirmedCount(forEntity: entityId) == 0 ? .synced : .pending
        }
        events[entityId] = event
    }

    /// Descarta uma mutação recusada (e as dependentes). Criação nunca aceita some junto com o registro.
    public mutating func discardRejected(mutationId: UUID) {
        guard let mutation = queue.mutation(mutationId), mutation.state == .rejected else { return }
        let entityId = mutation.entityId
        queue.removeAll(forEntity: entityId)
        guard var event = events[entityId] else { return }
        if event.version == 0 {
            removeEventAndWakes(entityId)
        } else {
            // Edição/exclusão recusada: o conteúdo local pode divergir do servidor até o próximo pull (Onda 5
            // deve rebuscar a entidade). Exclusão recusada devolve o registro à tela.
            event.deletedAt = nil
            event.syncStatus = .synced
            events[entityId] = event
        }
    }

    // MARK: Auxiliares

    private func wakeIds(of sleepId: UUID) -> [UUID] {
        events.values.filter { $0.kind == .wake && $0.sleepSessionId == sleepId }.map(\.id)
    }

    private mutating func removeEventAndWakes(_ id: UUID) {
        if events[id]?.kind == .sleep {
            for wake in wakeIds(of: id) {
                queue.removeAll(forEntity: wake)
                events[wake] = nil
            }
        }
        events[id] = nil
    }

    /// Diferença entre o estado antes e depois, para persistências que gravam só o que mudou.
    public func changes(from before: TrackingState) -> (upsertEvents: [TrackedEvent], removedEventIds: [UUID],
                                                         upsertMutations: [QueuedMutation], removedMutationIds: [UUID]) {
        let upserts = events.values.filter { before.events[$0.id] != $0 }
        let removedEvents = before.events.keys.filter { events[$0] == nil }
        let beforeMutations = Dictionary(uniqueKeysWithValues: before.queue.mutations.map { ($0.mutationId, $0) })
        let afterIds = Set(queue.mutations.map(\.mutationId))
        let upsertMutations = queue.mutations.filter { beforeMutations[$0.mutationId] != $0 }
        let removedMutations = beforeMutations.keys.filter { !afterIds.contains($0) }
        return (Array(upserts), Array(removedEvents), upsertMutations, Array(removedMutations))
    }
}
