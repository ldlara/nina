import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Informações do aparelho enviadas no login/refresh (sem identificadores de hardware).
public protocol DeviceInfoProviding: Sendable {
    func deviceInfo() -> DeviceInfo
}

public struct StaticDeviceInfoProvider: DeviceInfoProviding {
    public let info: DeviceInfo
    public init(info: DeviceInfo) { self.info = info }
    public func deviceInfo() -> DeviceInfo { info }
}

public enum SessionEndReason: Equatable, Sendable {
    /// Sessão revogada ou refresh token reutilizado/expirado. Os dados locais e mutações pendentes ficam
    /// intactos (RF-001-A4); o app só exige novo login.
    case revoked
    case userLoggedOut
}

public protocol APIClientProtocol: Sendable {
    func send<Response: Decodable & Sendable>(_ endpoint: Endpoint, as type: Response.Type) async throws -> Response
    func sendVoid(_ endpoint: Endpoint) async throws
}

/// Cliente HTTP do BFF. Responsabilidades:
/// - monta a requisição (base `/v1`, JSON snake_case, `Authorization: Bearer`);
/// - decodifica `application/problem+json` em `APIError.problem`;
/// - renova o access token (refresh rotativo, uso único) com *single-flight* e repete a chamada uma vez;
/// - sinaliza o fim da sessão sem apagar dados locais.
public actor APIClient: APIClientProtocol {
    private let baseURL: URL
    private let transport: HTTPTransport
    private let tokenStore: TokenStore
    private let deviceProvider: DeviceInfoProviding
    private let decoder: JSONDecoder
    private let now: @Sendable () -> Date
    private let onSessionEnded: @Sendable (SessionEndReason) -> Void
    private let refreshLeeway: TimeInterval = 30
    private var refreshTask: Task<StoredSession, Error>?

    public init(baseURL: URL, transport: HTTPTransport, tokenStore: TokenStore,
                deviceProvider: DeviceInfoProviding,
                now: @escaping @Sendable () -> Date = { Date() },
                onSessionEnded: @escaping @Sendable (SessionEndReason) -> Void = { _ in }) {
        self.baseURL = baseURL
        self.transport = transport
        self.tokenStore = tokenStore
        self.deviceProvider = deviceProvider
        self.decoder = NinaJSON.makeDecoder()
        self.now = now
        self.onSessionEnded = onSessionEnded
    }

    // MARK: - API pública

    public func send<Response: Decodable & Sendable>(_ endpoint: Endpoint, as type: Response.Type) async throws -> Response {
        let data = try await perform(endpoint)
        do {
            return try decoder.decode(Response.self, from: data)
        } catch {
            throw APIError.decoding(String(describing: error))
        }
    }

    public func sendVoid(_ endpoint: Endpoint) async throws {
        _ = try await perform(endpoint)
    }

    // MARK: - Execução

    private func perform(_ endpoint: Endpoint) async throws -> Data {
        var accessToken: String?
        if endpoint.requiresAuth {
            accessToken = try await validAccessToken()
        }
        let (data, status) = try await execute(endpoint, accessToken: accessToken)
        if (200..<300).contains(status) { return data }

        let problem = decodeProblem(data, status: status)
        if status == 401, endpoint.requiresAuth {
            if let code = problem?.code, code == ProblemCode.sessionRevoked || code == ProblemCode.refreshTokenReused {
                endSession(.revoked)
                throw APIError.problem(problem ?? Problem(code: code, status: 401), httpStatus: 401)
            }
            // Token expirado (ou 401 sem código conhecido): renova uma vez e repete.
            let renewed = try await refreshSession(replacing: accessToken)
            let (retryData, retryStatus) = try await execute(endpoint, accessToken: renewed.accessToken)
            if (200..<300).contains(retryStatus) { return retryData }
            throw makeError(data: retryData, status: retryStatus)
        }
        throw makeError(data: data, status: status)
    }

    private func execute(_ endpoint: Endpoint, accessToken: String?) async throws -> (Data, Int) {
        let request = try buildRequest(endpoint, accessToken: accessToken)
        do {
            let (data, response) = try await transport.send(request)
            return (data, response.statusCode)
        } catch let error as URLError {
            throw APIError.network(error)
        } catch let error as APIError {
            throw error
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw APIError.network(URLError(.unknown))
        }
    }

    private func buildRequest(_ endpoint: Endpoint, accessToken: String?) throws -> URLRequest {
        guard var components = URLComponents(url: baseURL, resolvingAgainstBaseURL: false) else {
            throw APIError.invalidRequest("baseURL")
        }
        components.path += endpoint.path
        components.queryItems = endpoint.query.isEmpty ? nil : endpoint.query
        guard let url = components.url else { throw APIError.invalidRequest(endpoint.path) }

        var request = URLRequest(url: url)
        request.httpMethod = endpoint.method.rawValue
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let body = endpoint.body {
            request.httpBody = body
            request.setValue(endpoint.contentType, forHTTPHeaderField: "Content-Type")
        }
        for (name, value) in endpoint.headers {
            request.setValue(value, forHTTPHeaderField: name)
        }
        if let accessToken {
            request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        }
        return request
    }

    private func decodeProblem(_ data: Data, status: Int) -> Problem? {
        guard !data.isEmpty else { return nil }
        return try? decoder.decode(Problem.self, from: data)
    }

    private func makeError(data: Data, status: Int) -> APIError {
        if let problem = decodeProblem(data, status: status) {
            return .problem(problem, httpStatus: status)
        }
        return .unexpectedStatus(status)
    }

    // MARK: - Sessão

    private func validAccessToken() async throws -> String {
        guard let session = try? tokenStore.load() else { throw APIError.notAuthenticated }
        if session.accessTokenExpiresAt.timeIntervalSince(now()) > refreshLeeway {
            return session.accessToken
        }
        return try await refreshSession(replacing: session.accessToken).accessToken
    }

    /// Renovação com *single-flight*: chamadas concorrentes aguardam a mesma renovação (o refresh token é de
    /// uso único, então duas renovações paralelas revogariam a família da sessão).
    private func refreshSession(replacing failingToken: String?) async throws -> StoredSession {
        if let refreshTask {
            return try await refreshTask.value
        }
        guard let current = try? tokenStore.load() else { throw APIError.notAuthenticated }
        // Outra chamada já renovou enquanto esta esperava resposta: reaproveita.
        if let failingToken, current.accessToken != failingToken,
           current.accessTokenExpiresAt.timeIntervalSince(now()) > refreshLeeway {
            return current
        }
        let task = Task { try await self.performRefresh(using: current) }
        refreshTask = task
        defer { refreshTask = nil }
        return try await task.value
    }

    private func performRefresh(using current: StoredSession) async throws -> StoredSession {
        let device = deviceProvider.deviceInfo()
        let body = RefreshRequest(refreshToken: current.refreshToken, deviceId: device.deviceId)
        let endpoint = try Endpoint.json(.post, "/auth/refresh", body: body, requiresAuth: false)
        let (data, status) = try await execute(endpoint, accessToken: nil)
        guard (200..<300).contains(status) else {
            let error = makeError(data: data, status: status)
            if status == 401 || status == 400 {
                // Refresh recusado: sessão encerrada. Tokens saem do Keychain; dados locais permanecem.
                endSession(.revoked)
            }
            throw error
        }
        let response: TokenResponse
        do {
            response = try decoder.decode(TokenResponse.self, from: data)
        } catch {
            throw APIError.decoding(String(describing: error))
        }
        let stored = StoredSession(response: response, now: now())
        try? tokenStore.save(stored)
        return stored
    }

    private func endSession(_ reason: SessionEndReason) {
        try? tokenStore.clear()
        onSessionEnded(reason)
    }
}
