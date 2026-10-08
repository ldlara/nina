import Foundation
import SwiftData
import NinaCore

/// Linha de evento. O JSON completo do `TrackedEvent` fica em `payload` (campo novo e aditivo do contrato não exige
/// migração de esquema); as colunas soltas existem só para consultar/ordenar sem decodificar tudo.
@Model
final class CachedEvent {
    @Attribute(.unique) var id: UUID
    var babyId: UUID
    var startAt: Date
    var kindRaw: String
    var sleepSessionId: UUID?
    var isDeleted: Bool
    var isOpenSleep: Bool
    var payload: Data

    init(event: TrackedEvent, payload: Data) {
        self.id = event.id
        self.babyId = event.babyId
        self.startAt = event.startAt
        self.kindRaw = event.kind.rawValue
        self.sleepSessionId = event.sleepSessionId
        self.isDeleted = event.isDeleted
        self.isOpenSleep = event.isOpenSleep
        self.payload = payload
    }

    func update(from event: TrackedEvent, payload: Data) {
        babyId = event.babyId
        startAt = event.startAt
        kindRaw = event.kind.rawValue
        sleepSessionId = event.sleepSessionId
        isDeleted = event.isDeleted
        isOpenSleep = event.isOpenSleep
        self.payload = payload
    }
}

/// Linha da fila de mutações (ADR-0003). O `QueuedMutation` inteiro fica no JSON; `sequence` define a ordem.
@Model
final class CachedMutation {
    @Attribute(.unique) var mutationId: UUID
    var sequence: Int
    var babyId: UUID
    var entityId: UUID
    var payload: Data

    init(mutation: QueuedMutation, payload: Data) {
        self.mutationId = mutation.mutationId
        self.sequence = mutation.sequence
        self.babyId = mutation.babyId
        self.entityId = mutation.entityId
        self.payload = payload
    }

    func update(from mutation: QueuedMutation, payload: Data) {
        sequence = mutation.sequence
        babyId = mutation.babyId
        entityId = mutation.entityId
        self.payload = payload
    }
}

/// `TrackingStore` em SwiftData. A semântica (fila, tombstones, resultados do push) é a de `TrackingState`/
/// `MutationQueueState` do NinaCore, já coberta por testes: cada operação composta carrega as linhas necessárias,
/// aplica a operação pura e grava só a diferença **em um único `save()`** (evento + mutação atômicos).
@ModelActor
actor SwiftDataTrackingStore: TrackingStore {
    // MARK: Leitura

    func event(id: UUID) async throws -> TrackedEvent? {
        try fetchEvent(id: id)
    }

    func events(babyId: UUID, from: Date?, to: Date?) async throws -> [TrackedEvent] {
        let lower = from ?? Date.distantPast
        let upper = to ?? Date.distantFuture
        let descriptor = FetchDescriptor<CachedEvent>(
            predicate: #Predicate { $0.babyId == babyId && !$0.isDeleted && $0.startAt >= lower && $0.startAt < upper },
            sortBy: [SortDescriptor(\.startAt, order: .reverse)])
        return try decode(try modelContext.fetch(descriptor)).sortedForTimeline()
    }

    func openSleep(babyId: UUID) async throws -> TrackedEvent? {
        let descriptor = FetchDescriptor<CachedEvent>(
            predicate: #Predicate { $0.babyId == babyId && $0.isOpenSleep && !$0.isDeleted })
        return try decode(try modelContext.fetch(descriptor)).sortedForTimeline().first
    }

    func wakeEvents(sleepSessionId: UUID) async throws -> [TrackedEvent] {
        let target: UUID? = sleepSessionId
        let descriptor = FetchDescriptor<CachedEvent>(
            predicate: #Predicate { $0.sleepSessionId == target && !$0.isDeleted },
            sortBy: [SortDescriptor(\.startAt)])
        return try decode(try modelContext.fetch(descriptor))
    }

    // MARK: Escrita local atômica

    func create(_ event: TrackedEvent, mutation: QueuedMutation) async throws {
        try transact(eventIds: [event.id]) { $0.create(event, mutation: mutation) }
    }

    func update(_ event: TrackedEvent, mutation: QueuedMutation) async throws {
        try transact(eventIds: [event.id]) { $0.update(event, mutation: mutation) }
    }

    func delete(eventId: UUID, at date: Date, mutation: QueuedMutation) async throws -> DeleteOutcome {
        try transact(eventIds: [eventId]) { $0.delete(eventId: eventId, at: date, mutation: mutation) }
    }

    func undoDelete(eventId: UUID) async throws -> Bool {
        try transact(eventIds: [eventId]) { $0.undoDelete(eventId: eventId) }
    }

    // MARK: Fila

    func nextBatch(now: Date, limit: Int, babyId: UUID?) async throws -> [QueuedMutation] {
        try loadQueue().nextBatch(now: now, limit: limit, babyId: babyId)
    }

    func markInFlight(_ ids: [UUID]) async throws {
        try transact(eventIds: []) { $0.queue.markInFlight(ids) }
    }

    func apply(_ results: [PushItemResult], now: Date, policy: RetryPolicy) async throws {
        let queue = try loadQueue()
        let entityIds = results.compactMap { queue.mutation($0.mutationId)?.entityId }
        try transact(eventIds: entityIds) { state in
            for result in results { state.apply(result, now: now, policy: policy) }
        }
    }

    func releaseInFlight(now: Date, policy: RetryPolicy, retryAfter: TimeInterval?) async throws {
        try transact(eventIds: []) { $0.queue.releaseInFlight(now: now, policy: policy, retryAfter: retryAfter) }
    }

    func recoverInFlight() async throws {
        try transact(eventIds: []) { $0.queue.recoverInFlight() }
    }

    func retryNow(babyId: UUID?) async throws {
        try transact(eventIds: []) { $0.queue.retryNow(babyId: babyId) }
    }

    func discardRejected(mutationId: UUID) async throws {
        let entity = try loadQueue().mutation(mutationId)?.entityId
        try transact(eventIds: entity.map { [$0] } ?? []) { $0.discardRejected(mutationId: mutationId) }
    }

    func pendingCount(babyId: UUID?) async throws -> Int {
        try loadQueue().pendingCount(babyId: babyId)
    }

    func rejectedMutations(babyId: UUID?) async throws -> [QueuedMutation] {
        try loadQueue().rejected(babyId: babyId)
    }

    func unconfirmedCount(forEntity entityId: UUID) async throws -> Int {
        try loadQueue().unconfirmedCount(forEntity: entityId)
    }

    func clear(babyId: UUID?) async throws {
        if let babyId {
            try modelContext.delete(model: CachedEvent.self, where: #Predicate { $0.babyId == babyId })
            try modelContext.delete(model: CachedMutation.self, where: #Predicate { $0.babyId == babyId })
        } else {
            try modelContext.delete(model: CachedEvent.self)
            try modelContext.delete(model: CachedMutation.self)
        }
        try modelContext.save()
    }

    // MARK: Interno

    private func fetchRow(id: UUID) throws -> CachedEvent? {
        try modelContext.fetch(FetchDescriptor<CachedEvent>(predicate: #Predicate { $0.id == id })).first
    }

    private func fetchEvent(id: UUID) throws -> TrackedEvent? {
        guard let row = try fetchRow(id: id) else { return nil }
        return try NinaStorageJSON.makeDecoder().decode(TrackedEvent.self, from: row.payload)
    }

    /// Linha ilegível (ex.: versão futura do app) é ignorada, não derruba a lista.
    private func decode(_ rows: [CachedEvent]) throws -> [TrackedEvent] {
        let decoder = NinaStorageJSON.makeDecoder()
        return rows.compactMap { try? decoder.decode(TrackedEvent.self, from: $0.payload) }
    }

    private func loadQueue() throws -> MutationQueueState {
        let rows = try modelContext.fetch(FetchDescriptor<CachedMutation>(sortBy: [SortDescriptor(\.sequence)]))
        let decoder = NinaStorageJSON.makeDecoder()
        // Mutação ilegível NÃO pode ser descartada em silêncio; o erro sobe e a sincronização para.
        return MutationQueueState(mutations: try rows.map { try decoder.decode(QueuedMutation.self, from: $0.payload) })
    }

    /// Carrega os eventos citados (e, para sonos, os despertares), roda a operação pura e grava a diferença.
    private func transact<Result>(eventIds: [UUID], _ body: (inout TrackingState) -> Result) throws -> Result {
        var events: [UUID: TrackedEvent] = [:]
        for id in eventIds {
            if let event = try fetchEvent(id: id) {
                events[id] = event
                if event.kind == .sleep {
                    let target: UUID? = id
                    let wakes = try modelContext.fetch(FetchDescriptor<CachedEvent>(
                        predicate: #Predicate { $0.sleepSessionId == target }))
                    for wake in try decode(wakes) { events[wake.id] = wake }
                }
            }
        }
        let before = TrackingState(events: events, queue: try loadQueue())
        var state = before
        let result = body(&state)
        let delta = state.changes(from: before)
        do {
            try persist(delta)
            try modelContext.save()
        } catch {
            modelContext.rollback()
            throw error
        }
        return result
    }

    private func persist(_ delta: (upsertEvents: [TrackedEvent], removedEventIds: [UUID],
                                   upsertMutations: [QueuedMutation], removedMutationIds: [UUID])) throws {
        let encoder = NinaStorageJSON.makeEncoder()
        for event in delta.upsertEvents {
            let payload = try encoder.encode(event)
            if let row = try fetchRow(id: event.id) {
                row.update(from: event, payload: payload)
            } else {
                modelContext.insert(CachedEvent(event: event, payload: payload))
            }
        }
        for id in delta.removedEventIds {
            if let row = try fetchRow(id: id) { modelContext.delete(row) }
        }
        for mutation in delta.upsertMutations {
            let payload = try encoder.encode(mutation)
            let id = mutation.mutationId
            let existing = try modelContext.fetch(FetchDescriptor<CachedMutation>(predicate: #Predicate { $0.mutationId == id })).first
            if let existing {
                existing.update(from: mutation, payload: payload)
            } else {
                modelContext.insert(CachedMutation(mutation: mutation, payload: payload))
            }
        }
        for id in delta.removedMutationIds {
            if let row = try modelContext.fetch(FetchDescriptor<CachedMutation>(predicate: #Predicate { $0.mutationId == id })).first {
                modelContext.delete(row)
            }
        }
    }
}
