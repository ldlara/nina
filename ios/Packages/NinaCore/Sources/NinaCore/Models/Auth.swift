import Foundation

public struct DeviceInfo: Codable, Hashable, Sendable {
    public var deviceId: UUID
    public var platform: DevicePlatform
    public var deviceLabel: String?
    public var appVersion: String?
    public var osVersion: String?

    public init(deviceId: UUID, platform: DevicePlatform = .ios, deviceLabel: String? = nil,
                appVersion: String? = nil, osVersion: String? = nil) {
        self.deviceId = deviceId
        self.platform = platform
        self.deviceLabel = deviceLabel
        self.appVersion = appVersion
        self.osVersion = osVersion
    }
}

public struct ConsentAcceptance: Codable, Hashable, Sendable {
    public var purposeKey: PurposeKey
    public var documentVersion: String

    public init(purposeKey: PurposeKey, documentVersion: String) {
        self.purposeKey = purposeKey
        self.documentVersion = documentVersion
    }
}

public struct RegisterRequest: Encodable, Equatable, Sendable {
    public var email: String
    public var password: String
    public var displayName: String?
    public var locale: String
    public var timezone: String?
    public var consents: [ConsentAcceptance]

    public init(email: String, password: String, displayName: String?, locale: String,
                timezone: String?, consents: [ConsentAcceptance]) {
        self.email = email
        self.password = password
        self.displayName = displayName
        self.locale = locale
        self.timezone = timezone
        self.consents = consents
    }
}

/// Resposta uniforme de `POST /auth/register` (202). `status` é tolerante.
public struct VerificationPending: Codable, Equatable, Sendable {
    public var status: String
    public var resendAfterSeconds: Int?

    public init(status: String = "VERIFICATION_PENDING", resendAfterSeconds: Int? = nil) {
        self.status = status
        self.resendAfterSeconds = resendAfterSeconds
    }
}

public struct EmailVerifyRequest: Encodable, Equatable, Sendable {
    public var email: String
    public var code: String
    public var device: DeviceInfo

    public init(email: String, code: String, device: DeviceInfo) {
        self.email = email
        self.code = code
        self.device = device
    }
}

public struct LoginRequest: Encodable, Equatable, Sendable {
    public var email: String
    public var password: String
    public var device: DeviceInfo

    public init(email: String, password: String, device: DeviceInfo) {
        self.email = email
        self.password = password
        self.device = device
    }
}

public struct SocialLoginRequest: Encodable, Equatable, Sendable {
    public var idToken: String
    public var nonce: String
    public var device: DeviceInfo
    public var givenName: String?
    public var familyName: String?
    public var locale: String?
    public var timezone: String?
    public var consents: [ConsentAcceptance]?

    public init(idToken: String, nonce: String, device: DeviceInfo, givenName: String? = nil,
                familyName: String? = nil, locale: String? = nil, timezone: String? = nil,
                consents: [ConsentAcceptance]? = nil) {
        self.idToken = idToken
        self.nonce = nonce
        self.device = device
        self.givenName = givenName
        self.familyName = familyName
        self.locale = locale
        self.timezone = timezone
        self.consents = consents
    }
}

public struct RefreshRequest: Encodable, Equatable, Sendable {
    public var refreshToken: String
    public var deviceId: UUID

    public init(refreshToken: String, deviceId: UUID) {
        self.refreshToken = refreshToken
        self.deviceId = deviceId
    }
}

public struct IdentityLink: Codable, Hashable, Sendable {
    public var provider: IdentityProvider
    public var linkedAt: Date
}

public struct User: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var email: String
    public var emailVerified: Bool
    public var displayName: String?
    public var locale: String
    public var timezone: String?
    public var status: UserStatus
    public var hasPassword: Bool
    public var identities: [IdentityLink]
    public var createdAt: Date

    public init(id: UUID, email: String, emailVerified: Bool, displayName: String?, locale: String,
                timezone: String?, status: UserStatus, hasPassword: Bool, identities: [IdentityLink],
                createdAt: Date) {
        self.id = id
        self.email = email
        self.emailVerified = emailVerified
        self.displayName = displayName
        self.locale = locale
        self.timezone = timezone
        self.status = status
        self.hasPassword = hasPassword
        self.identities = identities
        self.createdAt = createdAt
    }
}

public struct TokenResponse: Codable, Equatable, Sendable {
    public var tokenType: String
    public var accessToken: String
    public var expiresIn: Int
    public var refreshToken: String
    public var refreshExpiresAt: Date
    public var sessionId: UUID
    public var user: User
    public var pendingConsents: [ConsentAcceptance]?
}

public struct LegalDocument: Codable, Hashable, Sendable {
    public var purposeKey: PurposeKey
    public var version: String
    public var url: URL
    public var contentHash: String?
    public var effectiveAt: Date
    public var required: Bool?
}

public struct LegalDocumentsResponse: Codable, Equatable, Sendable {
    public var items: [LegalDocument]
}

public struct ConsentInput: Encodable, Equatable, Sendable {
    public var purposeKey: PurposeKey
    public var documentVersion: String
    public var status: ConsentStatus
    public var source: ConsentSource
    public var babyId: UUID?
    public var locale: String?

    public init(purposeKey: PurposeKey, documentVersion: String, status: ConsentStatus,
                source: ConsentSource, babyId: UUID? = nil, locale: String? = nil) {
        self.purposeKey = purposeKey
        self.documentVersion = documentVersion
        self.status = status
        self.source = source
        self.babyId = babyId
        self.locale = locale
    }
}

public struct ConsentRecord: Codable, Hashable, Sendable {
    public var id: UUID
    public var purposeKey: PurposeKey
    public var documentVersion: String
    public var status: String
    public var grantedAt: Date
}
