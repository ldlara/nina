import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
@testable import NinaCore

struct StubResponse {
    var status: Int
    var body: Data
    var headers: [String: String]

    init(status: Int = 200, json: String = "", headers: [String: String] = [:]) {
        self.status = status
        self.body = Data(json.utf8)
        var merged = headers
        if merged["Content-Type"] == nil {
            merged["Content-Type"] = status >= 400 ? "application/problem+json" : "application/json"
        }
        self.headers = merged
    }

    static func problem(_ status: Int, code: String, extra: String = "") -> StubResponse {
        StubResponse(status: status,
                     json: "{\"type\":\"https://api.nina.app/problems/x\",\"title\":\"t\",\"status\":\(status),\"code\":\"\(code)\"\(extra)}")
    }
}

struct RecordedRequest {
    var method: String
    var url: URL
    var headers: [String: String]
    var body: Data?

    var path: String { url.path }

    func header(_ name: String) -> String? {
        headers.first { $0.key.lowercased() == name.lowercased() }?.value
    }

    var bodyJSON: [String: Any]? {
        guard let body else { return nil }
        return (try? JSONSerialization.jsonObject(with: body)) as? [String: Any]
    }
}

/// Servidor falso global para `URLProtocol`. Os testes nunca tocam a rede real.
final class StubServer: @unchecked Sendable {
    static let shared = StubServer()

    private let lock = NSLock()
    private var handler: ((RecordedRequest) throws -> StubResponse)?
    private var recorded: [RecordedRequest] = []

    func install(_ handler: @escaping (RecordedRequest) throws -> StubResponse) {
        lock.lock(); defer { lock.unlock() }
        self.handler = handler
        recorded = []
    }

    func reset() {
        lock.lock(); defer { lock.unlock() }
        handler = nil
        recorded = []
    }

    var requests: [RecordedRequest] {
        lock.lock(); defer { lock.unlock() }
        return recorded
    }

    func count(path: String) -> Int { requests.filter { $0.path == path }.count }

    fileprivate func handle(_ request: RecordedRequest) throws -> StubResponse {
        lock.lock()
        recorded.append(request)
        let current = handler
        lock.unlock()
        guard let current else { throw URLError(.cannotConnectToHost) }
        return try current(request)
    }

    static func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: configuration)
    }
}

final class StubURLProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        var headers: [String: String] = [:]
        for (key, value) in request.allHTTPHeaderFields ?? [:] { headers[key] = value }
        let recorded = RecordedRequest(method: request.httpMethod ?? "GET", url: request.url!,
                                       headers: headers, body: Self.readBody(request))
        do {
            let stub = try StubServer.shared.handle(recorded)
            let response = HTTPURLResponse(url: request.url!, statusCode: stub.status, httpVersion: "HTTP/1.1",
                                           headerFields: stub.headers)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: stub.body)
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
    }

    override func stopLoading() {}

    /// Em URLSession o corpo chega como stream, não em `httpBody`.
    private static func readBody(_ request: URLRequest) -> Data? {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return nil }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let read = stream.read(&buffer, maxLength: buffer.count)
            if read <= 0 { break }
            data.append(buffer, count: read)
        }
        return data
    }
}
