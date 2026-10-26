import Foundation

public protocol CaregiverRepository: Sendable {
    func caregivers(babyId: UUID) async throws -> [Membership]
    func invite(babyId: UUID, email: String, role: InvitableRole, idempotencyKey: UUID) async throws -> Membership
    func resendInvitation(babyId: UUID, membershipId: UUID) async throws -> Membership
    func changeRole(babyId: UUID, membershipId: UUID, role: InvitableRole) async throws -> Membership
    /// Owner remove outro cuidador ou cancela convite; Caregiver/ReadOnly usa para sair (próprio vínculo).
    func remove(babyId: UUID, membershipId: UUID) async throws
    func inspectInvitation(token: String) async throws -> InvitationPreview
    func acceptInvitation(token: String) async throws -> Baby
    func declineInvitation(token: String) async throws
}

public final class DefaultCaregiverRepository: CaregiverRepository {
    private let client: APIClientProtocol

    public init(client: APIClientProtocol) { self.client = client }

    private func base(_ babyId: UUID) -> String { "/babies/\(babyId.uuidString.lowercased())" }

    public func caregivers(babyId: UUID) async throws -> [Membership] {
        let endpoint = Endpoint(method: .get, path: base(babyId) + "/caregivers")
        return try await client.send(endpoint, as: MembershipListResponse.self).items
    }

    public func invite(babyId: UUID, email: String, role: InvitableRole, idempotencyKey: UUID) async throws -> Membership {
        let endpoint = try Endpoint.json(.post, base(babyId) + "/invitations",
                                         body: InvitationCreate(email: email, role: role),
                                         headers: ["Idempotency-Key": idempotencyKey.uuidString.lowercased()])
        return try await client.send(endpoint, as: Membership.self)
    }

    public func resendInvitation(babyId: UUID, membershipId: UUID) async throws -> Membership {
        let endpoint = Endpoint(method: .post,
                                path: base(babyId) + "/invitations/\(membershipId.uuidString.lowercased())/resend")
        return try await client.send(endpoint, as: Membership.self)
    }

    public func changeRole(babyId: UUID, membershipId: UUID, role: InvitableRole) async throws -> Membership {
        let endpoint = try Endpoint.json(.patch, base(babyId) + "/caregivers/\(membershipId.uuidString.lowercased())",
                                         body: RoleChange(role: role))
        return try await client.send(endpoint, as: Membership.self)
    }

    public func remove(babyId: UUID, membershipId: UUID) async throws {
        let endpoint = Endpoint(method: .delete,
                                path: base(babyId) + "/caregivers/\(membershipId.uuidString.lowercased())")
        try await client.sendVoid(endpoint)
    }

    public func inspectInvitation(token: String) async throws -> InvitationPreview {
        let endpoint = try Endpoint.json(.post, "/invitations/inspect", body: InvitationToken(token: token))
        return try await client.send(endpoint, as: InvitationPreview.self)
    }

    public func acceptInvitation(token: String) async throws -> Baby {
        let endpoint = try Endpoint.json(.post, "/invitations/accept", body: InvitationToken(token: token))
        return try await client.send(endpoint, as: Baby.self)
    }

    public func declineInvitation(token: String) async throws {
        let endpoint = try Endpoint.json(.post, "/invitations/decline", body: InvitationToken(token: token))
        try await client.sendVoid(endpoint)
    }
}
