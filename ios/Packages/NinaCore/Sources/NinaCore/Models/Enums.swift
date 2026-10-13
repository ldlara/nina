import Foundation

// Enums do contrato (ADR-0010): valores em MAIÚSCULAS e extensíveis. O cliente nunca falha nem descarta o
// registro ao encontrar um valor novo: ele cai em `.unknown(raw)` e preserva o texto original.
// Cada enum implementa Codable à mão para não depender de síntese com valores associados.

/// Marcador dos enums tolerantes. Cada tipo oferece `init(rawValue:)` não falível: valores novos viram
/// `.unknown(raw)`.
public protocol TolerantEnum: RawRepresentable, Codable, Hashable, Sendable where RawValue == String {}

/// Papel do usuário no bebê.
public enum Role: TolerantEnum {
    case owner, caregiver, readOnly
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "OWNER": self = .owner
        case "CAREGIVER": self = .caregiver
        case "READ_ONLY": self = .readOnly
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .owner: return "OWNER"
        case .caregiver: return "CAREGIVER"
        case .readOnly: return "READ_ONLY"
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

    public var isOwner: Bool { self == .owner }
    /// Pode registrar/editar eventos (UX 4.7). Valor desconhecido é tratado como somente leitura.
    public var canWriteEvents: Bool { self == .owner || self == .caregiver }
}

/// Papéis que podem ser atribuídos em um convite.
public enum InvitableRole: TolerantEnum, CaseIterable {
    case caregiver, readOnly
    case unknown(String)

    public static let allCases: [InvitableRole] = [.caregiver, .readOnly]

    public init(rawValue: String) {
        switch rawValue {
        case "CAREGIVER": self = .caregiver
        case "READ_ONLY": self = .readOnly
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .caregiver: return "CAREGIVER"
        case .readOnly: return "READ_ONLY"
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

public enum MembershipStatus: TolerantEnum {
    case pending, active, revoked, declined, expired
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "PENDING": self = .pending
        case "ACTIVE": self = .active
        case "REVOKED": self = .revoked
        case "DECLINED": self = .declined
        case "EXPIRED": self = .expired
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .pending: return "PENDING"
        case .active: return "ACTIVE"
        case .revoked: return "REVOKED"
        case .declined: return "DECLINED"
        case .expired: return "EXPIRED"
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

public enum Sex: TolerantEnum, CaseIterable {
    case female, male, other
    case unknown(String)

    public static let allCases: [Sex] = [.female, .male, .other]

    public init(rawValue: String) {
        switch rawValue {
        case "FEMALE": self = .female
        case "MALE": self = .male
        case "OTHER": self = .other
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .female: return "FEMALE"
        case .male: return "MALE"
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

public enum IdentityProvider: TolerantEnum {
    case google, apple
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "GOOGLE": self = .google
        case "APPLE": self = .apple
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .google: return "GOOGLE"
        case .apple: return "APPLE"
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

public enum UserStatus: TolerantEnum {
    case active, pendingDeletion
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "ACTIVE": self = .active
        case "PENDING_DELETION": self = .pendingDeletion
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .active: return "ACTIVE"
        case .pendingDeletion: return "PENDING_DELETION"
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

public enum DevicePlatform: TolerantEnum {
    case ios, android
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "IOS": self = .ios
        case "ANDROID": self = .android
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .ios: return "IOS"
        case .android: return "ANDROID"
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

public enum PurposeKey: TolerantEnum {
    case termsOfUse, privacyPolicy, childDataGuardian, analyticsProduct, marketingEmail, pushNotifications
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "TERMS_OF_USE": self = .termsOfUse
        case "PRIVACY_POLICY": self = .privacyPolicy
        case "CHILD_DATA_GUARDIAN": self = .childDataGuardian
        case "ANALYTICS_PRODUCT": self = .analyticsProduct
        case "MARKETING_EMAIL": self = .marketingEmail
        case "PUSH_NOTIFICATIONS": self = .pushNotifications
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .termsOfUse: return "TERMS_OF_USE"
        case .privacyPolicy: return "PRIVACY_POLICY"
        case .childDataGuardian: return "CHILD_DATA_GUARDIAN"
        case .analyticsProduct: return "ANALYTICS_PRODUCT"
        case .marketingEmail: return "MARKETING_EMAIL"
        case .pushNotifications: return "PUSH_NOTIFICATIONS"
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

public enum ConsentStatus: TolerantEnum {
    case granted, revoked
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "GRANTED": self = .granted
        case "REVOKED": self = .revoked
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .granted: return "GRANTED"
        case .revoked: return "REVOKED"
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

public enum ConsentSource: TolerantEnum {
    case onboarding, settings, prompt
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "ONBOARDING": self = .onboarding
        case "SETTINGS": self = .settings
        case "PROMPT": self = .prompt
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .onboarding: return "ONBOARDING"
        case .settings: return "SETTINGS"
        case .prompt: return "PROMPT"
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
