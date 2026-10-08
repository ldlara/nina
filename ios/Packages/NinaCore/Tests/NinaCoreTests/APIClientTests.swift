import XCTest
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
@testable import NinaCore

final class APIClientTests: XCTestCase {
    override func setUp() {
        super.setUp()
        StubServer.shared.reset()
    }

    override func tearDown() {
        StubServer.shared.reset()
        super.tearDown()
    }

    func testAttachesBearerAndDecodesResponse() async throws {
        StubServer.shared.install { _ in StubResponse(json: "{\"items\":[\(Fixtures.babyJSON)]}") }
        let env = TestEnvironment()
        let response = try await env.client.send(Endpoint(method: .get, path: "/babies"), as: BabyListResponse.self)
        XCTAssertEqual(response.items.count, 1)
        let request = try XCTUnwrap(StubServer.shared.requests.first)
        XCTAssertEqual(request.method, "GET")
        XCTAssertEqual(request.url.absoluteString, "https://api.test.invalid/v1/babies")
        XCTAssertEqual(request.header("Authorization"), "Bearer access-1")
        XCTAssertEqual(request.header("Accept"), "application/json")
    }

    func testUnauthenticatedEndpointsDoNotSendBearer() async throws {
        StubServer.shared.install { _ in StubResponse(status: 202, json: "{\"status\":\"VERIFICATION_PENDING\",\"resend_after_seconds\":60}") }
        let env = TestEnvironment()
        let endpoint = try Endpoint.json(.post, "/auth/register", body: ["email": "a@b.co"], requiresAuth: false)
        let pending = try await env.client.send(endpoint, as: VerificationPending.self)
        XCTAssertEqual(pending.resendAfterSeconds, 60)
        XCTAssertNil(StubServer.shared.requests.first?.header("Authorization"))
        XCTAssertEqual(StubServer.shared.requests.first?.header("Content-Type"), "application/json")
    }

    func testProblemJSONBecomesTypedError() async {
        StubServer.shared.install { _ in
            .problem(400, code: "VALIDATION_FAILED", extra: ",\"errors\":[{\"field\":\"data.display_name\",\"code\":\"REQUIRED\"}]")
        }
        let env = TestEnvironment()
        do {
            _ = try await env.client.send(Endpoint(method: .get, path: "/babies"), as: BabyListResponse.self)
            XCTFail("deveria falhar")
        } catch let error as APIError {
            XCTAssertEqual(error.problemCode, "VALIDATION_FAILED")
            XCTAssertEqual(error.httpStatus, 400)
            XCTAssertEqual(error.fieldErrors.first?.code, "REQUIRED")
        } catch {
            XCTFail("erro inesperado: \(error)")
        }
    }

    func testNonProblemErrorBodyBecomesUnexpectedStatus() async {
        StubServer.shared.install { _ in StubResponse(status: 502, json: "<html>bad gateway</html>", headers: ["Content-Type": "text/html"]) }
        let env = TestEnvironment()
        do {
            try await env.client.sendVoid(Endpoint(method: .post, path: "/auth/logout"))
            XCTFail("deveria falhar")
        } catch {
            XCTAssertEqual(error as? APIError, .unexpectedStatus(502))
        }
    }

    func testNoStoredSessionThrowsNotAuthenticatedWithoutNetworkCall() async {
        let env = TestEnvironment(session: nil)
        do {
            _ = try await env.client.send(Endpoint(method: .get, path: "/me"), as: User.self)
            XCTFail("deveria falhar")
        } catch {
            XCTAssertEqual(error as? APIError, .notAuthenticated)
        }
        XCTAssertTrue(StubServer.shared.requests.isEmpty)
    }

    func testExpiredAccessTokenIsRefreshedAndRequestRetriedOnce() async throws {
        StubServer.shared.install { request in
            switch request.path {
            case "/v1/babies":
                if request.header("Authorization") == "Bearer access-1" { return .problem(401, code: "TOKEN_EXPIRED") }
                return StubResponse(json: "{\"items\":[]}")
            case "/v1/auth/refresh":
                return StubResponse(json: Fixtures.tokenJSON(access: "access-2", refresh: "refresh-2"))
            default: return .problem(404, code: "NOT_FOUND")
            }
        }
        let env = TestEnvironment()
        let result = try await env.client.send(Endpoint(method: .get, path: "/babies"), as: BabyListResponse.self)
        XCTAssertTrue(result.items.isEmpty)

        XCTAssertEqual(StubServer.shared.count(path: "/v1/auth/refresh"), 1)
        XCTAssertEqual(StubServer.shared.count(path: "/v1/babies"), 2)
        let refresh = try XCTUnwrap(StubServer.shared.requests.first { $0.path == "/v1/auth/refresh" })
        XCTAssertEqual(refresh.bodyJSON?["refresh_token"] as? String, "refresh-1")
        XCTAssertEqual(refresh.bodyJSON?["device_id"] as? String, Fixtures.deviceId.uuidString)
        XCTAssertNil(refresh.header("Authorization"))
        XCTAssertEqual(StubServer.shared.requests.last?.header("Authorization"), "Bearer access-2")
        // Refresh token rotativo: o novo par substituiu o antigo.
        let stored = try XCTUnwrap(try env.tokens.load())
        XCTAssertEqual(stored.accessToken, "access-2")
        XCTAssertEqual(stored.refreshToken, "refresh-2")
        XCTAssertTrue(env.ended.all.isEmpty)
    }

    func testConcurrentRequestsShareASingleRefresh() async throws {
        // Access token já vencido: as duas chamadas disparam renovação proativa ao mesmo tempo.
        let expired = Fixtures.session(accessExpiresIn: -10)
        StubServer.shared.install { request in
            switch request.path {
            case "/v1/auth/refresh":
                return StubResponse(json: Fixtures.tokenJSON(access: "access-2", refresh: "refresh-2"))
            default:
                return request.header("Authorization") == "Bearer access-2"
                    ? StubResponse(json: "{\"items\":[]}") : .problem(401, code: "TOKEN_EXPIRED")
            }
        }
        let env = TestEnvironment(session: expired)
        async let first = env.client.send(Endpoint(method: .get, path: "/babies"), as: BabyListResponse.self)
        async let second = env.client.send(Endpoint(method: .get, path: "/babies"), as: BabyListResponse.self)
        _ = try await (first, second)
        XCTAssertEqual(StubServer.shared.count(path: "/v1/auth/refresh"), 1,
                       "refresh token é de uso único: refresh paralelo revogaria a família da sessão")
    }

    func testSessionRevokedClearsTokensAndNotifies() async throws {
        StubServer.shared.install { _ in .problem(401, code: "SESSION_REVOKED") }
        let env = TestEnvironment()
        do {
            _ = try await env.client.send(Endpoint(method: .get, path: "/babies"), as: BabyListResponse.self)
            XCTFail("deveria falhar")
        } catch let error as APIError {
            XCTAssertEqual(error.problemCode, "SESSION_REVOKED")
        }
        XCTAssertNil(try env.tokens.load())
        XCTAssertEqual(env.ended.all, [.revoked])
        XCTAssertEqual(StubServer.shared.count(path: "/v1/auth/refresh"), 0)
    }

    func testRefreshRejectedEndsSession() async throws {
        StubServer.shared.install { request in
            request.path == "/v1/auth/refresh" ? .problem(401, code: "REFRESH_TOKEN_REUSED") : .problem(401, code: "TOKEN_EXPIRED")
        }
        let env = TestEnvironment()
        do {
            _ = try await env.client.send(Endpoint(method: .get, path: "/babies"), as: BabyListResponse.self)
            XCTFail("deveria falhar")
        } catch let error as APIError {
            XCTAssertEqual(error.problemCode, "REFRESH_TOKEN_REUSED")
        }
        XCTAssertNil(try env.tokens.load())
        XCTAssertEqual(env.ended.all, [.revoked])
    }

    func testOfflineDuringRefreshKeepsSession() async throws {
        StubServer.shared.install { request in
            if request.path == "/v1/auth/refresh" { throw URLError(.notConnectedToInternet) }
            return .problem(401, code: "TOKEN_EXPIRED")
        }
        let env = TestEnvironment()
        do {
            _ = try await env.client.send(Endpoint(method: .get, path: "/babies"), as: BabyListResponse.self)
            XCTFail("deveria falhar")
        } catch let error as APIError {
            XCTAssertTrue(error.isOffline)
        }
        XCTAssertNotNil(try env.tokens.load(), "offline não pode deslogar nem apagar a sessão (RF-001-A4)")
        XCTAssertTrue(env.ended.all.isEmpty)
    }

    func testTransportFailureMapsToNetworkError() async {
        StubServer.shared.install { _ in throw URLError(.timedOut) }
        let env = TestEnvironment()
        do {
            _ = try await env.client.send(Endpoint(method: .get, path: "/babies"), as: BabyListResponse.self)
            XCTFail("deveria falhar")
        } catch {
            guard case .network(let urlError)? = error as? APIError else { return XCTFail("esperava .network, veio \(error)") }
            XCTAssertEqual(urlError.code, .timedOut)
        }
    }

    func testInvalidCredentialsOnLoginDoesNotTriggerRefresh() async {
        StubServer.shared.install { _ in .problem(401, code: "INVALID_CREDENTIALS") }
        let env = TestEnvironment()
        do {
            let endpoint = try Endpoint.json(.post, "/auth/login", body: ["email": "a@b.co"], requiresAuth: false)
            _ = try await env.client.send(endpoint, as: TokenResponse.self)
            XCTFail("deveria falhar")
        } catch let error as APIError {
            XCTAssertEqual(error.problemCode, "INVALID_CREDENTIALS")
        } catch {
            XCTFail("erro inesperado")
        }
        XCTAssertEqual(StubServer.shared.requests.count, 1)
        XCTAssertTrue(env.ended.all.isEmpty)
    }
}
