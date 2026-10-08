import Foundation

public struct UserRef: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var displayName: String
}

public struct Membership: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var babyId: UUID
    /// `nil` enquanto o convite não foi aceito.
    public var user: UserRef?
    /// Visível apenas ao Owner.
    public var invitedEmail: String?
    public var role: Role
    public var status: MembershipStatus
    public var invitedAt: Date
    public var invitationExpiresAt: Date?
    public var acceptedAt: Date?

    public init(id: UUID, babyId: UUID, user: UserRef? = nil, invitedEmail: String? = nil, role: Role,
                status: MembershipStatus, invitedAt: Date, invitationExpiresAt: Date? = nil,
                acceptedAt: Date? = nil) {
        self.id = id
        self.babyId = babyId
        self.user = user
        self.invitedEmail = invitedEmail
        self.role = role
        self.status = status
        self.invitedAt = invitedAt
        self.invitationExpiresAt = invitationExpiresAt
        self.acceptedAt = acceptedAt
    }
}

public struct MembershipListResponse: Codable, Equatable, Sendable {
    public var items: [Membership]
}

public struct InvitationCreate: Encodable, Equatable, Sendable {
    public var email: String
    public var role: InvitableRole

    public init(email: String, role: InvitableRole) {
        self.email = email
        self.role = role
    }
}

public struct InvitationToken: Encodable, Equatable, Sendable {
    public var token: String

    public init(token: String) { self.token = token }
}

public struct InvitationPreview: Codable, Hashable, Sendable {
    public var inviterDisplayName: String
    public var babyLabel: String
    public var role: InvitableRole
    public var expiresAt: Date
    public var visibleData: [String]?

    public init(inviterDisplayName: String, babyLabel: String, role: InvitableRole, expiresAt: Date,
                visibleData: [String]? = nil) {
        self.inviterDisplayName = inviterDisplayName
        self.babyLabel = babyLabel
        self.role = role
        self.expiresAt = expiresAt
        self.visibleData = visibleData
    }
}

struct RoleChange: Encodable { var role: InvitableRole }
