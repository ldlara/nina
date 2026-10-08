import Foundation

/// JSON dinâmico para o `data` das mutações (o objeto muda conforme `entity_type` e `op`).
///
/// ATENÇÃO: `NinaJSON.makeDecoder()` usa `convertFromSnakeCase`, que também converte chaves de dicionário.
/// Por isso um `JSONValue` NUNCA deve ser lido/escrito com os coders do `NinaJSON`: use `NinaStorageJSON`
/// (sem estratégia de chaves). As chaves daqui já são `snake_case` exatamente como o contrato.
public enum JSONValue: Codable, Hashable, Sendable {
    case null
    case bool(Bool)
    case int(Int)
    case double(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Int.self) {
            self = .int(value)
        } else if let value = try? container.decode(Double.self) {
            self = .double(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([JSONValue].self) {
            self = .array(value)
        } else if let value = try? container.decode([String: JSONValue].self) {
            self = .object(value)
        } else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "JSON inválido")
        }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let value): try container.encode(value)
        case .int(let value): try container.encode(value)
        case .double(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .object(let value): try container.encode(value)
        }
    }

    public subscript(key: String) -> JSONValue? {
        if case .object(let dict) = self { return dict[key] }
        return nil
    }

    public var stringValue: String? {
        if case .string(let value) = self { return value }
        return nil
    }

    public var intValue: Int? {
        if case .int(let value) = self { return value }
        return nil
    }

    public var isNull: Bool {
        if case .null = self { return true }
        return false
    }

    public var objectKeys: Set<String> {
        if case .object(let dict) = self { return Set(dict.keys) }
        return []
    }

    /// Instante RFC 3339 em UTC (segundos), no formato do contrato.
    public static func instant(_ date: Date) -> JSONValue { .string(NinaJSON.formatInstant(date)) }
    public static func optionalInstant(_ date: Date?) -> JSONValue { date.map(instant) ?? .null }
}

/// Coders para persistência local e corpo de `/sync/push`: sem conversão de chaves (as chaves já são
/// as do contrato ou nomes locais) e datas como segundos desde 1970 (ida e volta exata).
public enum NinaStorageJSON {
    public static func makeEncoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .secondsSince1970
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }

    public static func makeDecoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .secondsSince1970
        return decoder
    }
}
