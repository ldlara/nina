import Foundation

/// Dados que cada escrita precisa conhecer do bebê selecionado.
public struct TrackingContext: Equatable, Sendable {
    public var babyId: UUID
    /// IANA do bebê (fuso vigente, gravado em cada evento novo: RB-014).
    public var timeZone: String
    public var role: Role

    public init(babyId: UUID, timeZone: String, role: Role) {
        self.babyId = babyId
        self.timeZone = timeZone
        self.role = role
    }

    public init(baby: Baby) {
        self.init(babyId: baby.id, timeZone: baby.timezone, role: baby.myRole)
    }
}

public enum EventError: Error, Equatable, Sendable {
    case validation([FieldIssue])
    case notFound
    /// Papel sem permissão de escrita (ReadOnly ou desconhecido), INV-13.
    case forbidden
    /// Já existe um timer de sono em curso para o bebê (RF-009-A3): o app leva o usuário ao existente.
    case sleepAlreadyOpen(TrackedEvent)
    case noOpenSleep
    /// A exclusão já foi enviada: não dá mais para desfazer.
    case cannotUndo
}

public struct QueueStatus: Equatable, Sendable {
    public var pending: Int
    public var rejected: Int

    public init(pending: Int, rejected: Int) {
        self.pending = pending
        self.rejected = rejected
    }

    public static let empty = QueueStatus(pending: 0, rejected: 0)
}

/// Dados locais que devem sair do aparelho no logout explícito (privacidade em aparelho compartilhado).
public protocol LocalDataClearing: Sendable {
    func clearLocalData() async
}

/// Repositório offline-first de eventos: **toda escrita vai primeiro para o banco local** (evento + mutação, de
/// forma atômica) e nunca depende de rede (RF-046). O envio é responsabilidade do `SyncEngine`.
public protocol EventRepository: LocalDataClearing {
    func events(babyId: UUID, from: Date?, to: Date?) async throws -> [TrackedEvent]
    func openSleep(babyId: UUID) async throws -> TrackedEvent?
    func wakeEvents(sleepSessionId: UUID) async throws -> [TrackedEvent]

    func create(_ draft: EventDraft, context: TrackingContext) async throws -> TrackedEvent
    func update(eventId: UUID, changes: EventChanges, context: TrackingContext) async throws -> TrackedEvent
    func delete(eventId: UUID, context: TrackingContext) async throws -> DeleteOutcome
    func undoDelete(eventId: UUID) async throws
    /// Recria (mesmo id) um registro que nunca saiu do aparelho e foi descartado: "Desfazer" de uma exclusão.
    func restore(_ event: TrackedEvent, context: TrackingContext) async throws

    func queueStatus(babyId: UUID?) async throws -> QueueStatus
    func retryPendingNow(babyId: UUID?) async throws
    func discardRejected(babyId: UUID?) async throws
}

public final class DefaultEventRepository: EventRepository {
    private let store: TrackingStore
    private let deviceId: UUID
    private let now: NowProvider
    private let makeId: @Sendable () -> UUID
    private let author: @Sendable () -> UserRef?
    /// Segura o `DELETE` na fila durante a janela de "Desfazer" (UX 2, princípio 7: 8 s).
    private let undoHold: TimeInterval

    public init(store: TrackingStore, deviceId: UUID, now: @escaping NowProvider = NinaClock.system,
                makeId: @escaping @Sendable () -> UUID = { UUID() },
                author: @escaping @Sendable () -> UserRef? = { nil }, undoHold: TimeInterval = 8) {
        self.store = store
        self.deviceId = deviceId
        self.now = now
        self.makeId = makeId
        self.author = author
        self.undoHold = undoHold
    }

    // MARK: Leitura

    public func events(babyId: UUID, from: Date?, to: Date?) async throws -> [TrackedEvent] {
        try await store.events(babyId: babyId, from: from, to: to)
    }

    public func openSleep(babyId: UUID) async throws -> TrackedEvent? { try await store.openSleep(babyId: babyId) }

    public func wakeEvents(sleepSessionId: UUID) async throws -> [TrackedEvent] {
        try await store.wakeEvents(sleepSessionId: sleepSessionId)
    }

    // MARK: Escrita

    public func create(_ draft: EventDraft, context: TrackingContext) async throws -> TrackedEvent {
        guard context.role.canWriteEvents else { throw EventError.forbidden }
        let current = now()
        let event = draft.build(id: makeId(), babyId: context.babyId, tz: context.timeZone, now: current, author: author())

        var issues = EventValidator.validate(event, now: current)
        if event.kind == .wake {
            guard let sleepId = event.sleepSessionId, let sleep = try await store.event(id: sleepId),
                  sleep.babyId == context.babyId, sleep.kind == .sleep, !sleep.isDeleted else {
                throw EventError.validation([FieldIssue(field: "sleepSessionId", code: "invalid")])
            }
            issues += EventValidator.validateWake(event, within: sleep, now: current)
        }
        guard issues.isEmpty else { throw EventError.validation(issues) }

        if event.isOpenSleep, let open = try await store.openSleep(babyId: context.babyId) {
            throw EventError.sleepAlreadyOpen(open)
        }

        let mutation = MutationFactory.create(for: event, mutationId: makeId(), deviceId: deviceId, now: current)
        try await store.create(event, mutation: mutation)
        return event
    }

    public func update(eventId: UUID, changes: EventChanges, context: TrackingContext) async throws -> TrackedEvent {
        guard context.role.canWriteEvents else { throw EventError.forbidden }
        guard !changes.isEmpty else { throw EventError.validation([FieldIssue(field: "changes", code: "required")]) }
        guard let existing = try await store.event(id: eventId), !existing.isDeleted, existing.babyId == context.babyId else {
            throw EventError.notFound
        }
        let current = now()
        var event = existing
        let inapplicable = Self.apply(changes, to: &event)
        event.updatedAt = current.roundedToSecond
        event.lastModifiedBy = author() ?? existing.lastModifiedBy
        event.syncStatus = .pending

        var issues = inapplicable + EventValidator.validate(event, now: current)
        if event.kind == .wake, let sleepId = event.sleepSessionId, let sleep = try await store.event(id: sleepId) {
            issues += EventValidator.validateWake(event, within: sleep, now: current)
        }
        guard issues.isEmpty else { throw EventError.validation(issues) }

        // Evita outro timer aberto ao reabrir um sono (limpar `end_at`).
        if event.isOpenSleep, !existing.isOpenSleep, let open = try await store.openSleep(babyId: context.babyId),
           open.id != event.id {
            throw EventError.sleepAlreadyOpen(open)
        }

        let base = try await predictedBaseVersion(for: existing)
        let mutation = MutationFactory.update(for: event, changes: changes, baseVersion: base, mutationId: makeId(),
                                              deviceId: deviceId, now: current)
        try await store.update(event, mutation: mutation)
        return event
    }

    public func delete(eventId: UUID, context: TrackingContext) async throws -> DeleteOutcome {
        guard context.role.canWriteEvents else { throw EventError.forbidden }
        guard let existing = try await store.event(id: eventId), !existing.isDeleted, existing.babyId == context.babyId else {
            throw EventError.notFound
        }
        let current = now()
        let base = try await predictedBaseVersion(for: existing)
        let mutation = MutationFactory.delete(for: existing, baseVersion: base, mutationId: makeId(), deviceId: deviceId,
                                              now: current, notBefore: current.addingTimeInterval(undoHold))
        return try await store.delete(eventId: eventId, at: current.roundedToSecond, mutation: mutation)
    }

    public func undoDelete(eventId: UUID) async throws {
        guard try await store.undoDelete(eventId: eventId) else { throw EventError.cannotUndo }
    }

    public func restore(_ event: TrackedEvent, context: TrackingContext) async throws {
        guard context.role.canWriteEvents else { throw EventError.forbidden }
        guard event.babyId == context.babyId else { throw EventError.notFound }
        var restored = event
        restored.version = 0
        restored.syncStatus = .pending
        restored.deletedAt = nil
        let mutation = MutationFactory.create(for: restored, mutationId: makeId(), deviceId: deviceId, now: now())
        try await store.create(restored, mutation: mutation)
    }

    // MARK: Fila

    public func queueStatus(babyId: UUID?) async throws -> QueueStatus {
        let pending = try await store.pendingCount(babyId: babyId)
        let rejected = try await store.rejectedMutations(babyId: babyId).count
        return QueueStatus(pending: pending, rejected: rejected)
    }

    public func retryPendingNow(babyId: UUID?) async throws { try await store.retryNow(babyId: babyId) }

    public func discardRejected(babyId: UUID?) async throws {
        for mutation in try await store.rejectedMutations(babyId: babyId) {
            try await store.discardRejected(mutationId: mutation.mutationId)
        }
    }

    public func clearLocalData() async {
        try? await store.clear(babyId: nil)
    }

    // MARK: Internos

    /// `base_version` previsto: versão confirmada + mutações já na fila da entidade (cada uma incrementa a
    /// versão no servidor). Criação pendente (versão 0) + 1 edição => base 1, como no exemplo do contrato.
    private func predictedBaseVersion(for event: TrackedEvent) async throws -> Int {
        let unconfirmed = try await store.unconfirmedCount(forEntity: event.id)
        return event.version + unconfirmed
    }

    /// Aplica as alterações ao evento. Devolve problemas para alterações que não se aplicam ao tipo.
    static func apply(_ changes: EventChanges, to event: inout TrackedEvent) -> [FieldIssue] {
        var issues: [FieldIssue] = []
        func reject(_ field: String) { issues.append(FieldIssue(field: field, code: "not_applicable")) }

        if let type = changes.sleepType { if event.kind == .sleep { event.sleepType = type } else { reject("sleepType") } }
        if let start = changes.startAt { event.startAt = start.roundedToSecond }
        switch changes.endAt {
        case .unchanged: break
        case .set(let end):
            if event.kind == .diaper { reject("endAt") } else { event.endAt = end.roundedToSecond }
        case .clear:
            // Só sono (reabrir) e mamadeira/outros podem ficar sem fim; mamada no peito, bomba e despertar exigem.
            if event.kind == .sleep || (event.kind == .feeding && event.feedingType != .breastfeeding) {
                event.endAt = nil
            } else {
                reject("endAt")
            }
        }
        if let tz = changes.tz { event.tz = tz }

        switch changes.methodOrPlace {
        case .unchanged: break
        case .set(let value): if event.kind == .sleep { event.methodOrPlace = value.nilIfBlank } else { reject("methodOrPlace") }
        case .clear: if event.kind == .sleep { event.methodOrPlace = nil } else { reject("methodOrPlace") }
        }
        switch changes.notes {
        case .unchanged: break
        case .set(let value):
            if event.kind == .pumping || event.kind == .wake { reject("notes") } else { event.notes = value.nilIfBlank }
        case .clear: event.notes = nil
        }
        switch changes.side {
        case .unchanged: break
        case .set(let side):
            if event.kind == .pumping || (event.kind == .feeding && event.feedingType == .breastfeeding) {
                event.side = side
            } else { reject("side") }
        case .clear:
            if event.kind == .pumping { event.side = nil } else { reject("side") }
        }
        switch changes.volumeMl {
        case .unchanged: break
        case .set(let volume):
            if event.kind == .pumping || (event.kind == .feeding && event.feedingType == .bottle) {
                event.volumeMl = volume
            } else { reject("volumeMl") }
        case .clear:
            if event.kind == .pumping { event.volumeMl = nil } else { reject("volumeMl") }
        }
        switch changes.milkType {
        case .unchanged: break
        case .set(let milk): if event.feedingType == .bottle { event.milkType = milk } else { reject("milkType") }
        case .clear: if event.feedingType == .bottle { event.milkType = nil } else { reject("milkType") }
        }
        if let diaper = changes.diaperType { if event.kind == .diaper { event.diaperType = diaper } else { reject("diaperType") } }
        if let source = changes.wakeSource { if event.kind == .wake { event.wakeSource = source } else { reject("source") } }
        return issues
    }
}
