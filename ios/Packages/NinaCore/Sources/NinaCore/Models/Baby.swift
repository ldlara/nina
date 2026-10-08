import Foundation

public enum AgeDisplayed: TolerantEnum {
    case chronological, corrected
    case unknown(String)

    public init(rawValue: String) {
        switch rawValue {
        case "CHRONOLOGICAL": self = .chronological
        case "CORRECTED": self = .corrected
        default: self = .unknown(rawValue)
        }
    }

    public var rawValue: String {
        switch self {
        case .chronological: return "CHRONOLOGICAL"
        case .corrected: return "CORRECTED"
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

public struct AgeValue: Codable, Hashable, Sendable {
    public var days: Int
    public var weeks: Int
    public var months: Int

    public init(days: Int, weeks: Int, months: Int) {
        self.days = days
        self.weeks = weeks
        self.months = months
    }
}

/// Decomposição de idade para exibição (calculada pelo servidor na data local do bebê).
public struct Age: Codable, Hashable, Sendable {
    public var asOf: CivilDate
    public var chronological: AgeValue
    /// `nil` = a correção não se aplica (ADR-0009).
    public var corrected: AgeValue?
    public var displayed: AgeDisplayed?

    public init(asOf: CivilDate, chronological: AgeValue, corrected: AgeValue? = nil, displayed: AgeDisplayed? = nil) {
        self.asOf = asOf
        self.chronological = chronological
        self.corrected = corrected
        self.displayed = displayed
    }
}

/// Fonte canônica da idade (ADR-0009). A idade corrigida nunca é calculada nem persistida no cliente.
public struct AgeCalculation: Codable, Hashable, Sendable {
    public var chronologicalDays: Int
    /// `nil` = não se aplica. Nunca interpretar `nil` como 0.
    public var correctedDays: Int?
    public var correctionApplied: Bool

    public init(chronologicalDays: Int, correctedDays: Int?, correctionApplied: Bool) {
        self.chronologicalDays = chronologicalDays
        self.correctedDays = correctedDays
        self.correctionApplied = correctionApplied
    }
}

public struct Baby: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var displayName: String
    public var birthDate: CivilDate
    public var dueDate: CivilDate?
    public var sex: Sex?
    public var timezone: String
    public var photoRef: String?
    public var myRole: Role
    public var age: Age?
    public var ageCalculation: AgeCalculation?
    public var version: Int
    public var createdAt: Date
    public var updatedAt: Date

    public init(id: UUID, displayName: String, birthDate: CivilDate, dueDate: CivilDate? = nil, sex: Sex? = nil,
                timezone: String, photoRef: String? = nil, myRole: Role, age: Age? = nil,
                ageCalculation: AgeCalculation? = nil, version: Int, createdAt: Date, updatedAt: Date) {
        self.id = id
        self.displayName = displayName
        self.birthDate = birthDate
        self.dueDate = dueDate
        self.sex = sex
        self.timezone = timezone
        self.photoRef = photoRef
        self.myRole = myRole
        self.age = age
        self.ageCalculation = ageCalculation
        self.version = version
        self.createdAt = createdAt
        self.updatedAt = updatedAt
    }
}

public struct BabyCreate: Encodable, Equatable, Sendable {
    public var id: UUID?
    public var displayName: String
    public var birthDate: CivilDate
    public var dueDate: CivilDate?
    public var sex: Sex?
    public var timezone: String?

    public init(id: UUID? = nil, displayName: String, birthDate: CivilDate, dueDate: CivilDate? = nil,
                sex: Sex? = nil, timezone: String? = nil) {
        self.id = id
        self.displayName = displayName
        self.birthDate = birthDate
        self.dueDate = dueDate
        self.sex = sex
        self.timezone = timezone
    }
}

/// Valor de um campo em merge-patch (RFC 7396): ausente, definido ou apagado (`null`).
public enum Patch<Value: Codable & Equatable & Sendable>: Equatable, Sendable {
    case unchanged
    case set(Value)
    case clear
}

/// Merge-patch de `PATCH /babies/{id}` (somente Owner). Campos opcionais ausentes não são enviados;
/// `Patch.clear` envia `null` explícito (ex.: remover `due_date`).
public struct BabyUpdate: Encodable, Equatable, Sendable {
    public var displayName: String?
    public var birthDate: CivilDate?
    public var dueDate: Patch<CivilDate>
    public var sex: Patch<Sex>
    public var timezone: String?

    public init(displayName: String? = nil, birthDate: CivilDate? = nil, dueDate: Patch<CivilDate> = .unchanged,
                sex: Patch<Sex> = .unchanged, timezone: String? = nil) {
        self.displayName = displayName
        self.birthDate = birthDate
        self.dueDate = dueDate
        self.sex = sex
        self.timezone = timezone
    }

    public var isEmpty: Bool {
        displayName == nil && birthDate == nil && dueDate == .unchanged && sex == .unchanged && timezone == nil
    }

    private enum CodingKeys: String, CodingKey {
        case displayName = "display_name"
        case birthDate = "birth_date"
        case dueDate = "due_date"
        case sex
        case timezone
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        if let displayName { try container.encode(displayName, forKey: .displayName) }
        if let birthDate { try container.encode(birthDate, forKey: .birthDate) }
        switch dueDate {
        case .unchanged: break
        case .set(let value): try container.encode(value, forKey: .dueDate)
        case .clear: try container.encodeNil(forKey: .dueDate)
        }
        switch sex {
        case .unchanged: break
        case .set(let value): try container.encode(value, forKey: .sex)
        case .clear: try container.encodeNil(forKey: .sex)
        }
        if let timezone { try container.encode(timezone, forKey: .timezone) }
    }
}

public struct BabyListResponse: Codable, Equatable, Sendable {
    public var items: [Baby]
}
