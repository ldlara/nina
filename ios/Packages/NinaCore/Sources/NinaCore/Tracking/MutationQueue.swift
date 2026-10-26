import Foundation

/// Mutação de escrita aguardando envio por `POST /sync/push` (ADR-0003: UUID, `client_created_at`,
/// `base_version`, `device_id`). O servidor decide a ordem de resolução; `client_created_at` é só informativo.
public struct QueuedMutation: Codable, Hashable, Sendable, Identifiable {
    /// Chave de idempotência (`mutation_id`). Nunca é regenerada em reenvios.
    public var mutationId: UUID
    /// Ordem local de inserção (atribuída pela fila, estritamente crescente). O push respeita esta ordem.
    public var sequence: Int
    public var op: MutationOp
    public var entityType: SyncEntityType
    public var entityId: UUID
    public var babyId: UUID
    /// Versão do servidor que o cliente conhecia (0 em criação).
    public var baseVersion: Int
    public var clientCreatedAt: Date
    public var deviceId: UUID
    /// `data` do contrato (CREATE: dados completos; UPDATE: `EventPatch`; DELETE: `nil`).
    public var data: JSONValue?
    /// Entidade que precisa existir antes (ex.: sessão de sono de um despertar). Bloqueia o envio até lá.
    public var dependsOn: UUID?
    public var state: MutationState
    public var attemptCount: Int
    /// Não enviar antes deste instante (backoff, ou janela de "Desfazer" de uma exclusão).
    public var nextAttemptAt: Date?
    public var lastErrorCode: String?

    public var id: UUID { mutationId }

    public init(mutationId: UUID = UUID(), sequence: Int = 0, op: MutationOp, entityType: SyncEntityType,
                entityId: UUID, babyId: UUID, baseVersion: Int, clientCreatedAt: Date, deviceId: UUID,
                data: JSONValue? = nil, dependsOn: UUID? = nil, state: MutationState = .pending,
                attemptCount: Int = 0, nextAttemptAt: Date? = nil, lastErrorCode: String? = nil) {
        self.mutationId = mutationId
        self.sequence = sequence
        self.op = op
        self.entityType = entityType
        self.entityId = entityId
        self.babyId = babyId
        self.baseVersion = baseVersion
        self.clientCreatedAt = clientCreatedAt
        self.deviceId = deviceId
        self.data = data
        self.dependsOn = dependsOn
        self.state = state
        self.attemptCount = attemptCount
        self.nextAttemptAt = nextAttemptAt
        self.lastErrorCode = lastErrorCode
    }
}

/// Backoff exponencial para reenvio (RNF-007). Determinístico; o jitter é injetado (testes usam identidade).
public struct RetryPolicy: Sendable {
    public var baseDelay: TimeInterval
    public var multiplier: Double
    /// Teto do contrato para `Retry-After`/backoff: 300 s.
    public var maxDelay: TimeInterval
    public var jitter: @Sendable (TimeInterval) -> TimeInterval

    public init(baseDelay: TimeInterval = 2, multiplier: Double = 2, maxDelay: TimeInterval = 300,
                jitter: @escaping @Sendable (TimeInterval) -> TimeInterval = { $0 }) {
        self.baseDelay = baseDelay
        self.multiplier = multiplier
        self.maxDelay = maxDelay
        self.jitter = jitter
    }

    /// Atraso antes da tentativa seguinte a `attempt` falhas (1 = primeira falha).
    public func delay(afterFailures attempt: Int) -> TimeInterval {
        let exponent = Double(max(0, attempt - 1))
        let raw = min(maxDelay, baseDelay * pow(multiplier, exponent))
        return min(maxDelay, max(0, jitter(raw)))
    }
}

/// Lógica pura da fila de mutações (sem I/O). `InMemoryTrackingStore` a usa direto; o SwiftData carrega as linhas,
/// aplica estas operações e grava a diferença. Assim a semântica é a mesma e fica coberta por teste em Linux/macOS.
public struct MutationQueueState: Equatable, Sendable {
    public private(set) var mutations: [QueuedMutation]
    public private(set) var nextSequence: Int

    public init(mutations: [QueuedMutation] = []) {
        let sorted = mutations.sorted { $0.sequence < $1.sequence }
        self.mutations = sorted
        self.nextSequence = (sorted.last?.sequence ?? 0) + 1
    }

    // MARK: Inserção

    /// Idempotente por `mutationId`: reinserir o mesmo UUID não duplica nem muda a ordem (devolve `false`).
    @discardableResult
    public mutating func enqueue(_ mutation: QueuedMutation) -> Bool {
        guard !mutations.contains(where: { $0.mutationId == mutation.mutationId }) else { return false }
        var stored = mutation
        stored.sequence = nextSequence
        nextSequence += 1
        mutations.append(stored)
        return true
    }

    // MARK: Consulta

    public func mutation(_ id: UUID) -> QueuedMutation? { mutations.first { $0.mutationId == id } }

    public func mutations(forEntity entityId: UUID) -> [QueuedMutation] { mutations.filter { $0.entityId == entityId } }

    /// Mutações não confirmadas e não recusadas (contador de "aguardando envio").
    public func pendingCount(babyId: UUID? = nil) -> Int {
        mutations.filter { $0.state != .rejected && (babyId == nil || $0.babyId == babyId) }.count
    }

    public func rejected(babyId: UUID? = nil) -> [QueuedMutation] {
        mutations.filter { $0.state == .rejected && (babyId == nil || $0.babyId == babyId) }
    }

    /// Quantas mutações não recusadas existem para a entidade (base da versão prevista do próximo `base_version`).
    public func unconfirmedCount(forEntity entityId: UUID) -> Int {
        mutations.filter { $0.entityId == entityId && $0.state != .rejected }.count
    }

    /// Próximo lote a enviar, em ordem. Regras:
    /// - só `pending` com `nextAttemptAt` vencido;
    /// - nada de uma entidade (ou de quem depende dela) passa à frente de uma mutação anterior dela que não
    ///   pode ser enviada agora (em voo, em espera ou recusada), preservando a ordem por entidade.
    public func nextBatch(now: Date, limit: Int = 100, babyId: UUID? = nil) -> [QueuedMutation] {
        var batch: [QueuedMutation] = []
        var blocked: Set<UUID> = []
        for mutation in mutations {
            if let babyId, mutation.babyId != babyId { continue }
            let dependencyBlocked = mutation.dependsOn.map { blocked.contains($0) } ?? false
            if blocked.contains(mutation.entityId) || dependencyBlocked {
                blocked.insert(mutation.entityId)
                continue
            }
            let ready = mutation.state == .pending && (mutation.nextAttemptAt.map { $0 <= now } ?? true)
            if ready {
                if batch.count < limit { batch.append(mutation) } else { break }
            } else {
                blocked.insert(mutation.entityId)
            }
        }
        return batch
    }

    // MARK: Transições

    public mutating func markInFlight(_ ids: [UUID]) {
        let set = Set(ids)
        for index in mutations.indices where set.contains(mutations[index].mutationId) {
            mutations[index].state = .inFlight
        }
    }

    /// O servidor aplicou (`APPLIED` ou `DUPLICATE`): remove a mutação e corrige o `base_version` das seguintes
    /// da mesma entidade (`versão canônica + posição`), pois cada uma ainda incrementará a versão.
    @discardableResult
    public mutating func markApplied(_ id: UUID, serverVersion: Int?) -> QueuedMutation? {
        guard let index = mutations.firstIndex(where: { $0.mutationId == id }) else { return nil }
        let applied = mutations.remove(at: index)
        if let serverVersion {
            var offset = 0
            for i in mutations.indices where mutations[i].entityId == applied.entityId
                && mutations[i].sequence > applied.sequence && mutations[i].state != .rejected {
                mutations[i].baseVersion = serverVersion + offset
                offset += 1
            }
        }
        return applied
    }

    /// Falha de uma mutação. `retryable=true` volta a `pending` com backoff (honra `Retry-After`);
    /// `false` marca `rejected` (descarte definitivo do envio; o app mostra e permite descartar).
    public mutating func markFailed(_ id: UUID, retryable: Bool, code: String?, now: Date,
                                    policy: RetryPolicy = RetryPolicy(), retryAfter: TimeInterval? = nil) {
        guard let index = mutations.firstIndex(where: { $0.mutationId == id }) else { return }
        mutations[index].lastErrorCode = code
        if retryable {
            mutations[index].state = .pending
            mutations[index].attemptCount += 1
            let delay = max(policy.delay(afterFailures: mutations[index].attemptCount), retryAfter ?? 0)
            mutations[index].nextAttemptAt = now.addingTimeInterval(min(delay, policy.maxDelay))
        } else {
            mutations[index].state = .rejected
            mutations[index].nextAttemptAt = nil
        }
    }

    /// Falha de transporte no envio de um lote: todas as em voo voltam a `pending` com backoff.
    public mutating func releaseInFlight(now: Date, policy: RetryPolicy = RetryPolicy(), retryAfter: TimeInterval? = nil) {
        for id in mutations.filter({ $0.state == .inFlight }).map(\.mutationId) {
            markFailed(id, retryable: true, code: "TRANSPORT", now: now, policy: policy, retryAfter: retryAfter)
        }
    }

    /// Na abertura do app: o que ficou "em voo" por queda do processo volta a `pending`, sem penalidade.
    /// O reenvio é seguro: o servidor responde `DUPLICATE` para `mutation_id` já aplicado.
    public mutating func recoverInFlight() {
        for index in mutations.indices where mutations[index].state == .inFlight {
            mutations[index].state = .pending
        }
    }

    /// "Tentar agora": zera esperas de backoff das pendentes.
    public mutating func retryNow(babyId: UUID? = nil) {
        for index in mutations.indices where mutations[index].state == .pending
            && (babyId == nil || mutations[index].babyId == babyId) {
            mutations[index].nextAttemptAt = nil
        }
    }

    /// Reenfileira uma mutação recusada (o usuário pediu para tentar de novo).
    public mutating func requeueRejected(_ id: UUID) {
        guard let index = mutations.firstIndex(where: { $0.mutationId == id }), mutations[index].state == .rejected else { return }
        mutations[index].state = .pending
        mutations[index].nextAttemptAt = nil
        mutations[index].lastErrorCode = nil
    }

    // MARK: Remoção

    /// Remove todas as mutações da entidade e as que dependem dela. Devolve os ids removidos.
    @discardableResult
    public mutating func removeAll(forEntity entityId: UUID) -> [UUID] {
        let removed = mutations.filter { $0.entityId == entityId || $0.dependsOn == entityId }.map(\.mutationId)
        mutations.removeAll { $0.entityId == entityId || $0.dependsOn == entityId }
        return removed
    }

    /// Se a criação da entidade ainda não saiu do aparelho (`pending`), nada chegou ao servidor: descarta
    /// criação, edições e dependentes. Devolve `true` se descartou (excluir não precisa de `DELETE`).
    @discardableResult
    public mutating func discardUnsentEntity(_ entityId: UUID) -> Bool {
        guard let create = mutations.first(where: { $0.entityId == entityId && $0.op == .create }),
              create.state == .pending else { return false }
        removeAll(forEntity: entityId)
        return true
    }

    /// Desfaz uma exclusão ainda não enviada (janela de "Desfazer"). Devolve `true` se removeu o `DELETE`.
    @discardableResult
    public mutating func cancelPendingDelete(forEntity entityId: UUID) -> Bool {
        guard let index = mutations.firstIndex(where: { $0.entityId == entityId && $0.op == .delete && $0.state == .pending })
        else { return false }
        mutations.remove(at: index)
        return true
    }

    public mutating func removeAll(forBaby babyId: UUID?) {
        if let babyId { mutations.removeAll { $0.babyId == babyId } } else { mutations.removeAll() }
    }
}
