import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public enum HTTPMethod: String, Sendable {
    case get = "GET", post = "POST", put = "PUT", patch = "PATCH", delete = "DELETE"
}

/// Descrição de uma chamada à API. O corpo já vem codificado (snake_case) por `NinaJSON`.
public struct Endpoint: Sendable {
    public var method: HTTPMethod
    public var path: String
    public var query: [URLQueryItem]
    public var body: Data?
    public var contentType: String
    public var headers: [String: String]
    public var requiresAuth: Bool

    public init(method: HTTPMethod, path: String, query: [URLQueryItem] = [], body: Data? = nil,
                contentType: String = "application/json", headers: [String: String] = [:],
                requiresAuth: Bool = true) {
        self.method = method
        self.path = path
        self.query = query
        self.body = body
        self.contentType = contentType
        self.headers = headers
        self.requiresAuth = requiresAuth
    }

    public static func json<Body: Encodable>(_ method: HTTPMethod, _ path: String, body: Body,
                                             contentType: String = "application/json",
                                             headers: [String: String] = [:],
                                             requiresAuth: Bool = true) throws -> Endpoint {
        let data = try NinaJSON.makeEncoder().encode(body)
        return Endpoint(method: method, path: path, body: data, contentType: contentType,
                        headers: headers, requiresAuth: requiresAuth)
    }
}

/// Abstração de rede. Em produção, `URLSessionTransport`; nos testes, `URLSession` com `URLProtocol` stub
/// (sem rede real) ou um fake desta interface.
public protocol HTTPTransport: Sendable {
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse)
}

public struct URLSessionTransport: HTTPTransport, @unchecked Sendable {
    private let session: URLSession

    public init(session: URLSession = .shared) {
        self.session = session
    }

    public func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        #if canImport(FoundationNetworking)
        // swift-corelibs-foundation (Linux): usa a API com callback para não depender de overloads async.
        return try await withCheckedThrowingContinuation { continuation in
            let task = session.dataTask(with: request) { data, response, error in
                if let error {
                    continuation.resume(throwing: error)
                } else if let data, let http = response as? HTTPURLResponse {
                    continuation.resume(returning: (data, http))
                } else {
                    continuation.resume(throwing: URLError(.badServerResponse))
                }
            }
            task.resume()
        }
        #else
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        return (data, http)
        #endif
    }
}

public enum APIError: Error, Equatable, Sendable {
    /// Falha de rede/conectividade. Dados locais e mutações pendentes são preservados.
    case network(URLError)
    /// Resposta `application/problem+json` do contrato.
    case problem(Problem, httpStatus: Int)
    case unexpectedStatus(Int)
    case decoding(String)
    case notAuthenticated
    case invalidRequest(String)

    public var problemCode: String? {
        if case .problem(let problem, _) = self { return problem.code }
        return nil
    }

    public var httpStatus: Int? {
        switch self {
        case .problem(_, let status): return status
        case .unexpectedStatus(let status): return status
        default: return nil
        }
    }

    public var isOffline: Bool {
        if case .network = self { return true }
        return false
    }

    public var fieldErrors: [FieldError] {
        if case .problem(let problem, _) = self { return problem.errors }
        return []
    }
}
