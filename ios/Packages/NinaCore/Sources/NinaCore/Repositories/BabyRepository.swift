import Foundation

/// Cache local de bebês (leitura offline). Produção: SwiftData (app); testes: memória.
public protocol BabyCache: Sendable {
    func loadAll() async throws -> [Baby]
    func replaceAll(_ babies: [Baby]) async throws
    func upsert(_ baby: Baby) async throws
    func remove(id: Baby.ID) async throws
    func clear() async throws
}

public actor InMemoryBabyCache: BabyCache {
    private var storage: [Baby.ID: Baby] = [:]

    public init(babies: [Baby] = []) {
        for baby in babies { storage[baby.id] = baby }
    }

    public func loadAll() async throws -> [Baby] {
        storage.values.sorted { ($0.createdAt, $0.id.uuidString) < ($1.createdAt, $1.id.uuidString) }
    }

    public func replaceAll(_ babies: [Baby]) async throws {
        storage = Dictionary(babies.map { ($0.id, $0) }, uniquingKeysWith: { _, latest in latest })
    }

    public func upsert(_ baby: Baby) async throws { storage[baby.id] = baby }
    public func remove(id: Baby.ID) async throws { storage[id] = nil }
    public func clear() async throws { storage.removeAll() }
}

public struct BabyListResult: Equatable, Sendable {
    public var babies: [Baby]
    /// `true` quando a rede falhou e a lista veio do cache local.
    public var isFromCache: Bool
}

public protocol BabyRepository: Sendable {
    func cachedBabies() async -> [Baby]
    func refreshBabies() async throws -> BabyListResult
    func createBaby(_ create: BabyCreate, idempotencyKey: UUID) async throws -> Baby
    func updateBaby(_ baby: Baby, patch: BabyUpdate) async throws -> Baby
    func clearLocalData() async
}

public final class DefaultBabyRepository: BabyRepository {
    private let client: APIClientProtocol
    private let cache: BabyCache

    public init(client: APIClientProtocol, cache: BabyCache) {
        self.client = client
        self.cache = cache
    }

    public func cachedBabies() async -> [Baby] {
        (try? await cache.loadAll()) ?? []
    }

    public func refreshBabies() async throws -> BabyListResult {
        do {
            let response = try await client.send(Endpoint(method: .get, path: "/babies"), as: BabyListResponse.self)
            // Substitui o cache inteiro: bebê sem acesso deixa de existir localmente (RB-015).
            try? await cache.replaceAll(response.items)
            return BabyListResult(babies: response.items, isFromCache: false)
        } catch let error as APIError where error.isOffline {
            let cached = await cachedBabies()
            if cached.isEmpty { throw error }
            return BabyListResult(babies: cached, isFromCache: true)
        }
    }

    public func createBaby(_ create: BabyCreate, idempotencyKey: UUID) async throws -> Baby {
        let endpoint = try Endpoint.json(.post, "/babies", body: create,
                                         headers: ["Idempotency-Key": idempotencyKey.uuidString.lowercased()])
        let baby = try await client.send(endpoint, as: Baby.self)
        try? await cache.upsert(baby)
        return baby
    }

    public func updateBaby(_ baby: Baby, patch: BabyUpdate) async throws -> Baby {
        let endpoint = try Endpoint.json(.patch, "/babies/\(baby.id.uuidString.lowercased())", body: patch,
                                         contentType: "application/merge-patch+json",
                                         headers: ["If-Match": "\"\(baby.version)\""])
        let updated = try await client.send(endpoint, as: Baby.self)
        try? await cache.upsert(updated)
        return updated
    }

    public func clearLocalData() async {
        try? await cache.clear()
    }
}
