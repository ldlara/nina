import Foundation

// Enums de tracking do contrato (openapi v1.0.1). Mesmo padrão tolerante de `Enums.swift` (ADR-0009/0010):
// valor novo vira `.unknown(raw)`, nunca derruba o decode nem é descartado, e é reenviado como veio.

/// Tipo de sono (`SleepSession.sleep_type`).
public enum SleepType: TolerantEnum, CaseIterable {
    case nap, night
    case unknown(String)

    public static let allCases: [SleepType] = [.nap, .night]

    public init(rawValue: String) {
        switch rawValue {
        case "NAP": self = .nap
        case "NIGHT": self = .night
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .nap: return "NAP"
        case .night: return "NIGHT"
        case .unknown(let raw): return raw
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Origem do registro de sono (`source`).
public enum SleepSource: TolerantEnum {
    case timer, manual
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "TIMER": self = .timer
        case "MANUAL": self = .manual
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .timer: return "TIMER"
        case .manual: return "MANUAL"
        case .unknown(let raw): return raw
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Tipo de fralda (ADR-0009): WET = urina, DIRTY = fezes, MIXED = ambos, DRY = verificada sem nenhum, UNSPECIFIED = não informado. `UNKNOWN` fica reservado a migração.
public enum DiaperType: TolerantEnum, CaseIterable {
    case wet, dirty, mixed, dry, unspecified
    case unknown(String)

    public static let allCases: [DiaperType] = [.wet, .dirty, .mixed, .dry, .unspecified]

    public init(rawValue: String) {
        switch rawValue {
        case "WET": self = .wet
        case "DIRTY": self = .dirty
        case "MIXED": self = .mixed
        case "DRY": self = .dry
        case "UNSPECIFIED": self = .unspecified
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .wet: return "WET"
        case .dirty: return "DIRTY"
        case .mixed: return "MIXED"
        case .dry: return "DRY"
        case .unspecified: return "UNSPECIFIED"
        case .unknown(let raw): return raw
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Tipo de alimentação. Imutável após a criação (exclua e recrie).
public enum FeedingType: TolerantEnum {
    case breastfeeding, bottle, solid, other
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "BREASTFEEDING": self = .breastfeeding
        case "BOTTLE": self = .bottle
        case "SOLID": self = .solid
        case "OTHER": self = .other
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .breastfeeding: return "BREASTFEEDING"
        case .bottle: return "BOTTLE"
        case .solid: return "SOLID"
        case .other: return "OTHER"
        case .unknown(let raw): return raw
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Tipo de leite. Só se aplica a `BOTTLE`; nos demais é `null` (não se aplica).
public enum MilkType: TolerantEnum, CaseIterable {
    case breastMilk, formula, mixed, other, unspecified
    case unknown(String)

    public static let allCases: [MilkType] = [.breastMilk, .formula, .mixed, .other, .unspecified]

    public init(rawValue: String) {
        switch rawValue {
        case "BREAST_MILK": self = .breastMilk
        case "FORMULA": self = .formula
        case "MIXED": self = .mixed
        case "OTHER": self = .other
        case "UNSPECIFIED": self = .unspecified
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .breastMilk: return "BREAST_MILK"
        case .formula: return "FORMULA"
        case .mixed: return "MIXED"
        case .other: return "OTHER"
        case .unspecified: return "UNSPECIFIED"
        case .unknown(let raw): return raw
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Lado da amamentação ou da bomba.
public enum BreastSide: TolerantEnum, CaseIterable {
    case left, right, both
    case unknown(String)

    public static let allCases: [BreastSide] = [.left, .right, .both]

    public init(rawValue: String) {
        switch rawValue {
        case "LEFT": self = .left
        case "RIGHT": self = .right
        case "BOTH": self = .both
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .left: return "LEFT"
        case .right: return "RIGHT"
        case .both: return "BOTH"
        case .unknown(let raw): return raw
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Origem do despertar (`WakeEvent.source`).
public enum WakeSource: TolerantEnum {
    case manual, inferred, imported
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "MANUAL": self = .manual
        case "INFERRED": self = .inferred
        case "IMPORT": self = .imported
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .manual: return "MANUAL"
        case .inferred: return "INFERRED"
        case .imported: return "IMPORT"
        case .unknown(let raw): return raw
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Entidades sincronizáveis do `POST /sync/push`.
public enum SyncEntityType: TolerantEnum {
    case sleepSession, feedingSession, pumpingSession, diaperEvent, wakeEvent
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "SLEEP_SESSION": self = .sleepSession
        case "FEEDING_SESSION": self = .feedingSession
        case "PUMPING_SESSION": self = .pumpingSession
        case "DIAPER_EVENT": self = .diaperEvent
        case "WAKE_EVENT": self = .wakeEvent
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .sleepSession: return "SLEEP_SESSION"
        case .feedingSession: return "FEEDING_SESSION"
        case .pumpingSession: return "PUMPING_SESSION"
        case .diaperEvent: return "DIAPER_EVENT"
        case .wakeEvent: return "WAKE_EVENT"
        case .unknown(let raw): return raw
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Status por mutação na resposta do push.
public enum PushStatus: TolerantEnum {
    case applied, duplicate, rejected
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "APPLIED": self = .applied
        case "DUPLICATE": self = .duplicate
        case "REJECTED": self = .rejected
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .applied: return "APPLIED"
        case .duplicate: return "DUPLICATE"
        case .rejected: return "REJECTED"
        case .unknown(let raw): return raw
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Resolução de conflito devolvida pelo servidor (`Resolution`).
public enum SyncResolution: TolerantEnum {
    case noConflict, merged, lwwClientWon, lwwServerWon, deleteWins, keptBoth
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "NONE": self = .noConflict
        case "MERGED": self = .merged
        case "LWW_CLIENT_WON": self = .lwwClientWon
        case "LWW_SERVER_WON": self = .lwwServerWon
        case "DELETE_WINS": self = .deleteWins
        case "KEPT_BOTH": self = .keptBoth
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .noConflict: return "NONE"
        case .merged: return "MERGED"
        case .lwwClientWon: return "LWW_CLIENT_WON"
        case .lwwServerWon: return "LWW_SERVER_WON"
        case .deleteWins: return "DELETE_WINS"
        case .keptBoth: return "KEPT_BOTH"
        case .unknown(let raw): return raw
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Tipo de evento local (discriminador interno; não é enum do contrato).
public enum EventKind: String, Codable, Sendable, CaseIterable {
    case sleep, feeding, pumping, diaper, wake

    public var syncEntityType: SyncEntityType {
        switch self {
        case .sleep: return .sleepSession
        case .feeding: return .feedingSession
        case .pumping: return .pumpingSession
        case .diaper: return .diaperEvent
        case .wake: return .wakeEvent
        }
    }
}

/// Estado de sincronização de um evento no aparelho.
public enum EventSyncStatus: String, Codable, Sendable {
    /// Servidor já conhece a versão local.
    case synced
    /// Há mutação na fila (criação, edição ou exclusão) ainda não confirmada.
    case pending
    /// O servidor recusou a mutação de forma definitiva; o registro precisa de atenção do usuário.
    case rejected
}

public enum MutationOp: String, Codable, Sendable {
    case create = "CREATE"
    case update = "UPDATE"
    case delete = "DELETE"
}

/// Estado de uma mutação na fila local.
public enum MutationState: String, Codable, Sendable {
    /// Aguardando envio (ou reenvio após falha transitória, respeitando `nextAttemptAt`).
    case pending
    /// Enviada; resposta ainda não aplicada. Após queda do app volta a `pending` (reenvio é idempotente).
    case inFlight
    /// Recusa definitiva do servidor (`retryable=false`). Fica na fila para o usuário ver e descartar.
    case rejected
}
