import Foundation

/// Formato de rede do `POST /sync/push` (openapi v1.0.1). A Onda 5 só precisa enviar o corpo e passar a resposta
/// para `TrackingStore.apply`; tudo aqui é puro e testável sem rede.
public enum SyncWire {
    public static let maxMutationsPerPush = 100

    /// Corpo do push. Ordem = ordem da fila. Os horários vão em segundos (RFC 3339 UTC) e as chaves são as do contrato.
    public static func pushBody(deviceId: UUID, mutations: [QueuedMutation]) throws -> Data {
        let items: [JSONValue] = mutations.map { mutation in
            var object: [String: JSONValue] = [
                "mutation_id": .string(mutation.mutationId.uuidString.lowercased()),
                "op": .string(mutation.op.rawValue),
                "entity_type": .string(mutation.entityType.rawValue),
                "entity_id": .string(mutation.entityId.uuidString.lowercased()),
                "baby_id": .string(mutation.babyId.uuidString.lowercased()),
                "base_version": .int(mutation.baseVersion),
                "client_created_at": .instant(mutation.clientCreatedAt)
            ]
            if let data = mutation.data { object["data"] = data }
            return .object(object)
        }
        let body: JSONValue = .object([
            "device_id": .string(deviceId.uuidString.lowercased()),
            "mutations": .array(items)
        ])
        return try NinaStorageJSON.makeEncoder().encode(body)
    }
}

public struct FieldConflict: Decodable, Equatable, Sendable {
    public var field: String
    public var kept: String
}

public struct PushWarning: Decodable, Equatable, Sendable {
    public var code: String
    public var relatedEntityIds: [UUID]?
    public var skewSeconds: Int?
    public var toleranceSeconds: Int?
}

/// Resultado por mutação. O campo `entity` (estado canônico) é ignorado aqui; a reconciliação completa com o
/// estado do servidor (Onda 5) vem do pull.
public struct PushMutationResult: Decodable, Equatable, Sendable {
    public var mutationId: UUID
    public var status: PushStatus
    public var entityType: SyncEntityType?
    public var entityId: UUID?
    public var serverReceivedAt: Date?
    public var version: Int?
    public var resolution: SyncResolution?
    public var conflicts: [FieldConflict]?
    public var warnings: [PushWarning]?
    public var retryable: Bool?
    public var problem: Problem?

    public func itemResult(retryAfter: TimeInterval? = nil) -> PushItemResult {
        PushItemResult(mutationId: mutationId, status: status, version: version, retryable: retryable,
                       problemCode: problem?.code, retryAfter: retryAfter ?? problem?.retryAfterSeconds.map { TimeInterval($0) })
    }

    /// `CLIENT_CLOCK_SKEW`: o app deve avisar o usuário para corrigir o relógio (UX 4.13).
    public var hasClockSkewWarning: Bool { warnings?.contains { $0.code == "CLIENT_CLOCK_SKEW" } ?? false }
}

public struct PushResponse: Decodable, Equatable, Sendable {
    public var serverTime: Date
    public var results: [PushMutationResult]
}
