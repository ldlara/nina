import Foundation

public struct FieldError: Codable, Hashable, Sendable {
    public var field: String
    public var code: String
}

/// RFC 7807 do contrato. O app localiza a mensagem a partir de `code` (RF-050-A5); `title`/`detail` não
/// são exibidos ao usuário.
public struct Problem: Decodable, Equatable, Sendable {
    public var type: String?
    public var title: String?
    public var status: Int?
    public var detail: String?
    public var code: String
    public var requestId: String?
    public var errors: [FieldError]
    public var retryAfterSeconds: Int?
    public var requiredConsents: [ConsentAcceptance]?

    public init(code: String, status: Int? = nil, errors: [FieldError] = [], retryAfterSeconds: Int? = nil) {
        self.type = nil
        self.title = nil
        self.status = status
        self.detail = nil
        self.code = code
        self.requestId = nil
        self.errors = errors
        self.retryAfterSeconds = retryAfterSeconds
        self.requiredConsents = nil
    }

    private enum CodingKeys: String, CodingKey {
        case type, title, status, detail, code, requestId, errors, retryAfterSeconds, requiredConsents
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        type = try c.decodeIfPresent(String.self, forKey: .type)
        title = try c.decodeIfPresent(String.self, forKey: .title)
        status = try c.decodeIfPresent(Int.self, forKey: .status)
        detail = try c.decodeIfPresent(String.self, forKey: .detail)
        code = try c.decodeIfPresent(String.self, forKey: .code) ?? "UNKNOWN"
        requestId = try c.decodeIfPresent(String.self, forKey: .requestId)
        errors = try c.decodeIfPresent([FieldError].self, forKey: .errors) ?? []
        retryAfterSeconds = try c.decodeIfPresent(Int.self, forKey: .retryAfterSeconds)
        requiredConsents = try c.decodeIfPresent([ConsentAcceptance].self, forKey: .requiredConsents)
    }
}

/// Códigos estáveis do contrato que o app trata de forma especial. Outros códigos caem na mensagem genérica.
public enum ProblemCode {
    public static let validationFailed = "VALIDATION_FAILED"
    public static let tokenExpired = "TOKEN_EXPIRED"
    public static let sessionRevoked = "SESSION_REVOKED"
    public static let refreshTokenReused = "REFRESH_TOKEN_REUSED"
    public static let invalidCredentials = "INVALID_CREDENTIALS"
    public static let forbiddenRole = "FORBIDDEN_ROLE"
    public static let accessRevoked = "ACCESS_REVOKED"
    public static let consentRequired = "CONSENT_REQUIRED"
    public static let notFound = "NOT_FOUND"
    public static let identityLinkRequired = "IDENTITY_LINK_REQUIRED"
    public static let alreadyMember = "ALREADY_MEMBER"
    public static let versionConflict = "VERSION_CONFLICT"
    public static let rateLimited = "RATE_LIMITED"
    public static let clientUpgradeRequired = "CLIENT_UPGRADE_REQUIRED"
}
