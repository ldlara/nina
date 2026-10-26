import Foundation

/// Mensagem localizável pelo app. O core não carrega bundles: devolve a chave (`Localizable`) e argumentos.
public struct UserMessage: Equatable, Hashable, Sendable {
    public let key: String
    public let arguments: [String]

    public init(_ key: String, arguments: [String] = []) {
        self.key = key
        self.arguments = arguments
    }

    public static let generic = UserMessage("error.generic")
    public static let offline = UserMessage("error.offline")
}

public enum LoadState: Equatable, Sendable {
    case idle
    case loading
    case loaded
    case failed(UserMessage)
}

/// Traduz erros técnicos em chaves de mensagem. O texto vem sempre do `code` estável (RF-050-A5), nunca de
/// `title`/`detail` do servidor. Códigos desconhecidos caem na mensagem genérica.
public enum ErrorMapper {
    static let knownCodes: Set<String> = [
        "VALIDATION_FAILED", "INVALID_CREDENTIALS", "FORBIDDEN_ROLE", "ACCESS_REVOKED", "CONSENT_REQUIRED",
        "NOT_FOUND", "IDENTITY_LINK_REQUIRED", "ALREADY_MEMBER", "VERSION_CONFLICT", "RATE_LIMITED",
        "CLIENT_UPGRADE_REQUIRED", "SESSION_REVOKED", "TOKEN_EXPIRED", "REFRESH_TOKEN_REUSED",
        "IDEMPOTENCY_KEY_REUSE", "OWNER_REQUIRED"
    ]

    public static func message(for error: Error) -> UserMessage {
        if let socialError = error as? SocialSignInError {
            switch socialError {
            case .cancelled: return UserMessage("error.social_cancelled")
            case .notConfigured: return UserMessage("error.social_not_configured")
            case .failed: return UserMessage("error.social_failed")
            }
        }
        guard let api = error as? APIError else { return .generic }
        switch api {
        case .network: return .offline
        case .notAuthenticated: return UserMessage("error.session_revoked")
        case .problem(let problem, _):
            if knownCodes.contains(problem.code) {
                return UserMessage("error." + problem.code.lowercased())
            }
            return .generic
        case .unexpectedStatus, .decoding, .invalidRequest:
            return .generic
        }
    }

    /// Erros por campo (`errors[]` do Problem) indexados pelo último segmento do caminho (`data.password` -> `password`).
    public static func fieldMessages(for error: Error) -> [String: UserMessage] {
        guard let api = error as? APIError else { return [:] }
        var result: [String: UserMessage] = [:]
        for fieldError in api.fieldErrors {
            let name = fieldError.field.split(separator: ".").last.map(String.init) ?? fieldError.field
            guard result[name] == nil else { continue }
            result[name] = UserMessage("validation." + fieldError.code.lowercased())
        }
        return result
    }
}

/// Preferências simples (ex.: onboarding visto). Produção: UserDefaults; testes: memória.
public protocol PreferenceStore: Sendable {
    func bool(forKey key: String) -> Bool
    func set(_ value: Bool, forKey key: String)
}

public final class InMemoryPreferenceStore: PreferenceStore, @unchecked Sendable {
    private let lock = NSLock()
    private var values: [String: Bool] = [:]

    public init() {}

    public func bool(forKey key: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return values[key] ?? false
    }

    public func set(_ value: Bool, forKey key: String) {
        lock.lock(); defer { lock.unlock() }
        values[key] = value
    }
}

public enum Validators {
    /// Validação leve de formato (o servidor é a autoridade). Sem espaços, uma `@`, domínio com ponto.
    public static func isPlausibleEmail(_ raw: String) -> Bool {
        let email = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard email.count >= 5, email.count <= 254, !email.contains(" ") else { return false }
        let parts = email.split(separator: "@", omittingEmptySubsequences: false)
        guard parts.count == 2, !parts[0].isEmpty, parts[1].contains("."),
              !parts[1].hasPrefix("."), !parts[1].hasSuffix(".") else { return false }
        return true
    }

    /// Mínimo de 8 caracteres é suposição do cliente: a política real está em aberto (D-16) e o servidor decide.
    public static let minimumPasswordLength = 8

    public enum PasswordStrength: Equatable, Sendable {
        case empty, weak, fair, strong
    }

    public static func passwordStrength(_ password: String) -> PasswordStrength {
        guard !password.isEmpty else { return .empty }
        guard password.count >= minimumPasswordLength else { return .weak }
        var classes = 0
        if password.contains(where: { $0.isLowercase }) { classes += 1 }
        if password.contains(where: { $0.isUppercase }) { classes += 1 }
        if password.contains(where: { $0.isNumber }) { classes += 1 }
        if password.contains(where: { !$0.isLetter && !$0.isNumber }) { classes += 1 }
        if password.count >= 12 || classes >= 3 { return .strong }
        return .fair
    }
}
