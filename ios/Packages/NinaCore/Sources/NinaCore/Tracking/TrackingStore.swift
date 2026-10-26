import Foundation

/// Persistência local de eventos + fila de mutações. Produção: SwiftData (app, `SwiftDataTrackingStore`);
/// testes: `InMemoryTrackingStore`. Toda escrita composta é **atômica** (evento e mutação juntos): ou os dois
/// são gravados ou nenhum, para o app nunca ter um registro sem mutação (ou o contrário) após queda do processo.
public protocol TrackingStore: Sendable {
    // Leitura de eventos (já exclui tombstones locais, exceto `event(id:)`)
    func event(id: UUID) async throws -> TrackedEvent?
    /// Eventos do bebê em `[from, to)` por `startAt`, mais recentes primeiro (desempate por `id`).
    func events(babyId: UUID, from: Date?, to: Date?) async throws -> [TrackedEvent]
    func openSleep(babyId: UUID) async throws -> TrackedEvent?
    /// Despertares da sessão (não excluídos), em ordem cronológica.
    func wakeEvents(sleepSessionId: UUID) async throws -> [TrackedEvent]

    // Escrita local atômica (evento + mutação)
    func create(_ event: TrackedEvent, mutation: QueuedMutation) async throws
    func update(_ event: TrackedEvent, mutation: QueuedMutation) async throws
    func delete(eventId: UUID, at date: Date, mutation: QueuedMutation) async throws -> DeleteOutcome
    func undoDelete(eventId: UUID) async throws -> Bool

    // Fila
    func nextBatch(now: Date, limit: Int, babyId: UUID?) async throws -> [QueuedMutation]
    func markInFlight(_ ids: [UUID]) async throws
    func apply(_ results: [PushItemResult], now: Date, policy: RetryPolicy) async throws
    func releaseInFlight(now: Date, policy: RetryPolicy, retryAfter: TimeInterval?) async throws
    func recoverInFlight() async throws
    func retryNow(babyId: UUID?) async throws
    func discardRejected(mutationId: UUID) async throws
    func pendingCount(babyId: UUID?) async throws -> Int
    func rejectedMutations(babyId: UUID?) async throws -> [QueuedMutation]
    /// Quantas mutações não recusadas a entidade tem na fila (base do próximo `base_version`).
    func unconfirmedCount(forEntity entityId: UUID) async throws -> Int

    /// Apaga eventos e fila (logout explícito, acesso revogado, resync). `nil` = tudo.
    func clear(babyId: UUID?) async throws
}

public extension TrackingStore {
    func events(babyId: UUID) async throws -> [TrackedEvent] {
        try await events(babyId: babyId, from: nil, to: nil)
    }
}

extension Array where Element == TrackedEvent {
    /// Ordem da timeline: `startAt` decrescente, desempate por `id` (contrato `getTimeline`).
    public func sortedForTimeline() -> [TrackedEvent] {
        sorted { lhs, rhs in
            if lhs.startAt != rhs.startAt { return lhs.startAt > rhs.startAt }
            return lhs.id.uuidString > rhs.id.uuidString
        }
    }
}

public actor InMemoryTrackingStore: TrackingStore {
    private var state: TrackingState

    public init(events: [TrackedEvent] = [], mutations: [QueuedMutation] = []) {
        state = TrackingState(events: Dictionary(events.map { ($0.id, $0) }, uniquingKeysWith: { _, latest in latest }),
                              queue: MutationQueueState(mutations: mutations))
    }

    /// Usado por testes para inspecionar a fila inteira em ordem.
    public func allMutations() -> [QueuedMutation] { state.queue.mutations }
    public func allEvents(includeDeleted: Bool = true) -> [TrackedEvent] {
        state.events.values.filter { includeDeleted || !$0.isDeleted }.sortedForTimeline()
    }

    public func event(id: UUID) async throws -> TrackedEvent? { state.events[id] }

    public func events(babyId: UUID, from: Date?, to: Date?) async throws -> [TrackedEvent] {
        state.events.values.filter { event in
            event.babyId == babyId && !event.isDeleted
                && (from.map { event.startAt >= $0 } ?? true) && (to.map { event.startAt < $0 } ?? true)
        }.sortedForTimeline()
    }

    public func openSleep(babyId: UUID) async throws -> TrackedEvent? {
        state.events.values.filter { $0.babyId == babyId && $0.isOpenSleep }.sortedForTimeline().first
    }

    public func wakeEvents(sleepSessionId: UUID) async throws -> [TrackedEvent] {
        state.events.values.filter { $0.kind == .wake && $0.sleepSessionId == sleepSessionId && !$0.isDeleted }
            .sorted { $0.startAt < $1.startAt }
    }

    public func create(_ event: TrackedEvent, mutation: QueuedMutation) async throws {
        state.create(event, mutation: mutation)
    }

    public func update(_ event: TrackedEvent, mutation: QueuedMutation) async throws {
        state.update(event, mutation: mutation)
    }

    public func delete(eventId: UUID, at date: Date, mutation: QueuedMutation) async throws -> DeleteOutcome {
        state.delete(eventId: eventId, at: date, mutation: mutation)
    }

    public func undoDelete(eventId: UUID) async throws -> Bool { state.undoDelete(eventId: eventId) }

    public func nextBatch(now: Date, limit: Int, babyId: UUID?) async throws -> [QueuedMutation] {
        state.queue.nextBatch(now: now, limit: limit, babyId: babyId)
    }

    public func markInFlight(_ ids: [UUID]) async throws { state.queue.markInFlight(ids) }

    public func apply(_ results: [PushItemResult], now: Date, policy: RetryPolicy) async throws {
        for result in results { state.apply(result, now: now, policy: policy) }
    }

    public func releaseInFlight(now: Date, policy: RetryPolicy, retryAfter: TimeInterval?) async throws {
        state.queue.releaseInFlight(now: now, policy: policy, retryAfter: retryAfter)
    }

    public func recoverInFlight() async throws { state.queue.recoverInFlight() }
    public func retryNow(babyId: UUID?) async throws { state.queue.retryNow(babyId: babyId) }
    public func discardRejected(mutationId: UUID) async throws { state.discardRejected(mutationId: mutationId) }
    public func pendingCount(babyId: UUID?) async throws -> Int { state.queue.pendingCount(babyId: babyId) }
    public func rejectedMutations(babyId: UUID?) async throws -> [QueuedMutation] { state.queue.rejected(babyId: babyId) }

    public func unconfirmedCount(forEntity entityId: UUID) async throws -> Int {
        state.queue.unconfirmedCount(forEntity: entityId)
    }

    public func clear(babyId: UUID?) async throws {
        if let babyId {
            state.events = state.events.filter { $0.value.babyId != babyId }
        } else {
            state.events.removeAll()
        }
        state.queue.removeAll(forBaby: babyId)
    }
}
