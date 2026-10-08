import Foundation

/// Credencial obtida de um provedor social (Sign in with Apple / Google). O servidor valida assinatura,
/// `aud`, `iss`, expiração e `nonce` (ADR-0007); o app só repassa o `id_token` e o `nonce` original.
public struct SocialCredential: Equatable, Sendable {
    public var idToken: String
    public var nonce: String
    public var givenName: String?
    public var familyName: String?

    public init(idToken: String, nonce: String, givenName: String? = nil, familyName: String? = nil) {
        self.idToken = idToken
        self.nonce = nonce
        self.givenName = givenName
        self.familyName = familyName
    }
}

public enum SocialSignInError: Error, Equatable, Sendable {
    case cancelled
    /// SDK/credenciais do provedor ausentes neste build (ex.: Google sem `GoogleService-Info.plist`).
    case notConfigured
    case failed(String)
}

/// Provedor de login social atrás de protocolo: a UI não conhece AuthenticationServices nem o SDK do Google.
public protocol SocialSignInProvider: Sendable {
    var provider: IdentityProvider { get }
    func authenticate() async throws -> SocialCredential
}

/// Implementação de desenvolvimento/testes: devolve uma credencial fixa ou falha com o erro configurado.
public struct StubSocialSignInProvider: SocialSignInProvider {
    public let provider: IdentityProvider
    public let result: Result<SocialCredential, SocialSignInError>

    public init(provider: IdentityProvider,
                result: Result<SocialCredential, SocialSignInError> = .failure(.notConfigured)) {
        self.provider = provider
        self.result = result
    }

    public func authenticate() async throws -> SocialCredential {
        try result.get()
    }
}

public protocol AuthRepository: Sendable {
    /// `true` se há tokens guardados (não garante que ainda sejam válidos).
    func hasStoredSession() -> Bool
    func register(_ request: RegisterRequest) async throws -> VerificationPending
    func verifyEmail(email: String, code: String) async throws -> User
    func login(email: String, password: String) async throws -> User
    func signIn(with credential: SocialCredential, provider: IdentityProvider,
                consents: [ConsentAcceptance]?, locale: String?, timezone: String?) async throws -> User
    func requestPasswordReset(email: String) async throws
    func currentUser() async throws -> User
    func logout() async
}

public final class DefaultAuthRepository: AuthRepository {
    private let client: APIClientProtocol
    private let tokenStore: TokenStore
    private let deviceProvider: DeviceInfoProviding
    private let now: @Sendable () -> Date

    public init(client: APIClientProtocol, tokenStore: TokenStore, deviceProvider: DeviceInfoProviding,
                now: @escaping @Sendable () -> Date = { Date() }) {
        self.client = client
        self.tokenStore = tokenStore
        self.deviceProvider = deviceProvider
        self.now = now
    }

    public func hasStoredSession() -> Bool {
        (try? tokenStore.load()) != nil
    }

    public func register(_ request: RegisterRequest) async throws -> VerificationPending {
        let endpoint = try Endpoint.json(.post, "/auth/register", body: request,
                                         headers: ["Idempotency-Key": UUID().uuidString.lowercased()],
                                         requiresAuth: false)
        return try await client.send(endpoint, as: VerificationPending.self)
    }

    public func verifyEmail(email: String, code: String) async throws -> User {
        let body = EmailVerifyRequest(email: email, code: code, device: deviceProvider.deviceInfo())
        let endpoint = try Endpoint.json(.post, "/auth/email/verify", body: body, requiresAuth: false)
        return try await storeSession(from: endpoint)
    }

    public func login(email: String, password: String) async throws -> User {
        let body = LoginRequest(email: email, password: password, device: deviceProvider.deviceInfo())
        let endpoint = try Endpoint.json(.post, "/auth/login", body: body, requiresAuth: false)
        return try await storeSession(from: endpoint)
    }

    public func signIn(with credential: SocialCredential, provider: IdentityProvider,
                       consents: [ConsentAcceptance]?, locale: String?, timezone: String?) async throws -> User {
        let path: String
        switch provider {
        case .apple: path = "/auth/apple"
        case .google: path = "/auth/google"
        case .unknown: throw APIError.invalidRequest("provider")
        }
        let body = SocialLoginRequest(idToken: credential.idToken, nonce: credential.nonce,
                                      device: deviceProvider.deviceInfo(), givenName: credential.givenName,
                                      familyName: credential.familyName, locale: locale, timezone: timezone,
                                      consents: consents)
        let endpoint = try Endpoint.json(.post, path, body: body, requiresAuth: false)
        return try await storeSession(from: endpoint)
    }

    public func requestPasswordReset(email: String) async throws {
        struct Body: Encodable { var email: String }
        let endpoint = try Endpoint.json(.post, "/auth/password/forgot", body: Body(email: email), requiresAuth: false)
        try await client.sendVoid(endpoint)
    }

    public func currentUser() async throws -> User {
        try await client.send(Endpoint(method: .get, path: "/me"), as: User.self)
    }

    public func logout() async {
        // Melhor esforço: o servidor revoga a sessão; localmente os tokens saem de qualquer jeito.
        try? await client.sendVoid(Endpoint(method: .post, path: "/auth/logout"))
        try? tokenStore.clear()
    }

    private func storeSession(from endpoint: Endpoint) async throws -> User {
        let response = try await client.send(endpoint, as: TokenResponse.self)
        try tokenStore.save(StoredSession(response: response, now: now()))
        return response.user
    }
}
