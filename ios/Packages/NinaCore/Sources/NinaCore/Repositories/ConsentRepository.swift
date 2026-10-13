import Foundation

public protocol ConsentRepository: Sendable {
    func legalDocuments() async throws -> [LegalDocument]
    @discardableResult
    func record(_ input: ConsentInput) async throws -> ConsentRecord
}

public final class DefaultConsentRepository: ConsentRepository {
    private let client: APIClientProtocol

    public init(client: APIClientProtocol) { self.client = client }

    public func legalDocuments() async throws -> [LegalDocument] {
        let endpoint = Endpoint(method: .get, path: "/legal/documents", requiresAuth: false)
        return try await client.send(endpoint, as: LegalDocumentsResponse.self).items
    }

    @discardableResult
    public func record(_ input: ConsentInput) async throws -> ConsentRecord {
        let endpoint = try Endpoint.json(.post, "/me/consents", body: input)
        return try await client.send(endpoint, as: ConsentRecord.self)
    }
}
