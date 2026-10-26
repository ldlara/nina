import Foundation

/// Sessão do Nina guardada no aparelho. Access token curto (<= 15 min) e refresh token rotativo (SEC-011).
public struct StoredSession: Codable, Equatable, Sendable {
    public var accessToken: String
    public var accessTokenExpiresAt: Date
    public var refreshToken: String
    public var refreshTokenExpiresAt: Date
    public var sessionId: UUID
    public var userId: UUID

    public init(accessToken: String, accessTokenExpiresAt: Date, refreshToken: String,
                refreshTokenExpiresAt: Date, sessionId: UUID, userId: UUID) {
        self.accessToken = accessToken
        self.accessTokenExpiresAt = accessTokenExpiresAt
        self.refreshToken = refreshToken
        self.refreshTokenExpiresAt = refreshTokenExpiresAt
        self.sessionId = sessionId
        self.userId = userId
    }

    public init(response: TokenResponse, now: Date = Date()) {
        self.init(accessToken: response.accessToken,
                  accessTokenExpiresAt: now.addingTimeInterval(TimeInterval(response.expiresIn)),
                  refreshToken: response.refreshToken,
                  refreshTokenExpiresAt: response.refreshExpiresAt,
                  sessionId: response.sessionId,
                  userId: response.user.id)
    }
}

/// Armazenamento de tokens atrás de protocolo. Produção: Keychain. Testes: memória.
public protocol TokenStore: Sendable {
    func load() throws -> StoredSession?
    func save(_ session: StoredSession) throws
    func clear() throws
}

public final class InMemoryTokenStore: TokenStore, @unchecked Sendable {
    private let lock = NSLock()
    private var session: StoredSession?

    public init(session: StoredSession? = nil) {
        self.session = session
    }

    public func load() throws -> StoredSession? {
        lock.lock(); defer { lock.unlock() }
        return session
    }

    public func save(_ session: StoredSession) throws {
        lock.lock(); defer { lock.unlock() }
        self.session = session
    }

    public func clear() throws {
        lock.lock(); defer { lock.unlock() }
        session = nil
    }
}

#if canImport(Security)
import Security

public enum KeychainError: Error, Equatable {
    case unexpectedStatus(OSStatus)
    case corruptedData
}

/// Tokens no Keychain (`kSecClassGenericPassword`), acessíveis após o primeiro desbloqueio e sem migrar
/// para outro aparelho/backup.
public struct KeychainTokenStore: TokenStore {
    private let service: String
    private let account: String
    private let accessGroup: String?

    public init(service: String = "app.nina.session", account: String = "current", accessGroup: String? = nil) {
        self.service = service
        self.account = account
        self.accessGroup = accessGroup
    }

    private var baseQuery: [String: Any] {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        if let accessGroup { query[kSecAttrAccessGroup as String] = accessGroup }
        return query
    }

    public func load() throws -> StoredSession? {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw KeychainError.unexpectedStatus(status) }
        guard let data = result as? Data else { throw KeychainError.corruptedData }
        do {
            return try NinaJSON.makeDecoder().decode(StoredSession.self, from: data)
        } catch {
            throw KeychainError.corruptedData
        }
    }

    public func save(_ session: StoredSession) throws {
        let data = try NinaJSON.makeEncoder().encode(session)
        let attributes: [String: Any] = [kSecValueData as String: data]
        let updateStatus = SecItemUpdate(baseQuery as CFDictionary, attributes as CFDictionary)
        if updateStatus == errSecSuccess { return }
        guard updateStatus == errSecItemNotFound else { throw KeychainError.unexpectedStatus(updateStatus) }
        var add = baseQuery
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let addStatus = SecItemAdd(add as CFDictionary, nil)
        guard addStatus == errSecSuccess else { throw KeychainError.unexpectedStatus(addStatus) }
    }

    public func clear() throws {
        let status = SecItemDelete(baseQuery as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError.unexpectedStatus(status)
        }
    }
}
#endif
