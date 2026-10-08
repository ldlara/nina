import Foundation

public enum SyncOutcome: Equatable, Sendable {
    /// A Onda 5 (motor de sync de rede) ainda não existe: nada foi enviado nem recebido.
    case notImplemented
    case completed(sent: Int)
    case offline
    case failed(UserMessage)
}

/// Motor de sincronização (push/pull). **Fora do escopo do IOS-002:** só o contrato e um stub. A implementação
/// real (Onda 5) deve: reabrir a fila (`recoverInFlight`), enviar `nextBatch` em ordem, aplicar o resultado com
/// `TrackingStore.apply`, tratar `429`/`Retry-After`, fazer pull por cursor opaco (snapshot/delta/tombstone),
/// `410 SYNC_CURSOR_EXPIRED` (preservar a fila e refazer o snapshot) e `403 ACCESS_REVOKED` (apagar o bebê).
public protocol SyncEngine: Sendable {
    func syncNow(babyId: UUID?) async -> SyncOutcome
}

/// Implementação provisória: não faz rede. Mantém o app funcional (tudo fica "aguardando envio").
public struct StubSyncEngine: SyncEngine {
    public init() {}
    public func syncNow(babyId: UUID?) async -> SyncOutcome { .notImplemented }
}

/// Indicador de sincronização (UX 4.13): estado único, discreto, nunca modal.
public struct SyncIndicator: Equatable, Sendable {
    public enum Phase: Equatable, Sendable {
        case synced
        case syncing
        case offline
        /// Online com mutações aguardando envio (reenvio automático com backoff).
        case pending
        /// Falha persistente desde `since`.
        case error(since: Date)
    }

    public var phase: Phase
    public var pendingCount: Int
    /// Mutações recusadas em definitivo pelo servidor (precisam de atenção do usuário).
    public var rejectedCount: Int

    public init(phase: Phase, pendingCount: Int, rejectedCount: Int) {
        self.phase = phase
        self.pendingCount = pendingCount
        self.rejectedCount = rejectedCount
    }

    public static func resolve(isOnline: Bool, isSyncing: Bool, failingSince: Date?, pendingCount: Int,
                               rejectedCount: Int) -> SyncIndicator {
        let phase: Phase
        if isSyncing {
            phase = .syncing
        } else if !isOnline {
            phase = .offline
        } else if let failingSince {
            phase = .error(since: failingSince)
        } else if pendingCount > 0 {
            phase = .pending
        } else {
            phase = .synced
        }
        return SyncIndicator(phase: phase, pendingCount: pendingCount, rejectedCount: rejectedCount)
    }
}
