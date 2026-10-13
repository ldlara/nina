import XCTest
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
@testable import NinaCore

final class RepositoryTests: XCTestCase {
    override func setUp() {
        super.setUp()
        StubServer.shared.reset()
    }

    override func tearDown() {
        StubServer.shared.reset()
        super.tearDown()
    }

    // MARK: - Auth

    func testLoginStoresTokensAndSendsDevice() async throws {
        StubServer.shared.install { _ in StubResponse(json: Fixtures.tokenJSON(access: "a-new", refresh: "r-new")) }
        let env = TestEnvironment(session: nil)
        let repo = DefaultAuthRepository(client: env.client, tokenStore: env.tokens,
                                         deviceProvider: StaticDeviceInfoProvider(info: DeviceInfo(deviceId: Fixtures.deviceId)))
        XCTAssertFalse(repo.hasStoredSession())
        let user = try await repo.login(email: "ana@example.org", password: "senha-segura-123")
        XCTAssertEqual(user.email, "ana@example.org")
        XCTAssertTrue(repo.hasStoredSession())
        XCTAssertEqual(try env.tokens.load()?.accessToken, "a-new")
        XCTAssertEqual(try env.tokens.load()?.refreshToken, "r-new")

        let request = try XCTUnwrap(StubServer.shared.requests.first)
        XCTAssertEqual(request.path, "/v1/auth/login")
        XCTAssertEqual(request.method, "POST")
        XCTAssertNil(request.header("Authorization"))
        XCTAssertEqual(request.bodyJSON?["email"] as? String, "ana@example.org")
        XCTAssertEqual((request.bodyJSON?["device"] as? [String: Any])?["device_id"] as? String, Fixtures.deviceId.uuidString)
    }

    func testFailedLoginDoesNotStoreTokens() async throws {
        StubServer.shared.install { _ in .problem(401, code: "INVALID_CREDENTIALS") }
        let env = TestEnvironment(session: nil)
        let repo = DefaultAuthRepository(client: env.client, tokenStore: env.tokens,
                                         deviceProvider: StaticDeviceInfoProvider(info: DeviceInfo(deviceId: Fixtures.deviceId)))
        do {
            _ = try await repo.login(email: "ana@example.org", password: "errada")
            XCTFail("deveria falhar")
        } catch let error as APIError {
            XCTAssertEqual(error.problemCode, "INVALID_CREDENTIALS")
        }
        XCTAssertNil(try env.tokens.load())
    }

    func testRegisterDoesNotCreateSessionUntilEmailIsVerified() async throws {
        StubServer.shared.install { request in
            request.path.hasSuffix("/register")
                ? StubResponse(status: 202, json: "{\"status\":\"VERIFICATION_PENDING\",\"resend_after_seconds\":60}")
                : StubResponse(json: Fixtures.tokenJSON())
        }
        let env = TestEnvironment(session: nil)
        let repo = DefaultAuthRepository(client: env.client, tokenStore: env.tokens,
                                         deviceProvider: StaticDeviceInfoProvider(info: DeviceInfo(deviceId: Fixtures.deviceId)))
        let request = RegisterRequest(email: "ana@example.org", password: "senha-segura-123", displayName: "Ana", locale: "pt-BR",
                                      timezone: "America/Sao_Paulo",
                                      consents: [ConsentAcceptance(purposeKey: .termsOfUse, documentVersion: "1.0.0"),
                                                 ConsentAcceptance(purposeKey: .privacyPolicy, documentVersion: "1.0.0")])
        let pending = try await repo.register(request)
        XCTAssertEqual(pending.status, "VERIFICATION_PENDING")
        XCTAssertFalse(repo.hasStoredSession())
        XCTAssertNotNil(StubServer.shared.requests.first?.header("Idempotency-Key"))

        _ = try await repo.verifyEmail(email: "ana@example.org", code: "123456")
        XCTAssertTrue(repo.hasStoredSession())
        let verify = try XCTUnwrap(StubServer.shared.requests.last)
        XCTAssertEqual(verify.path, "/v1/auth/email/verify")
        XCTAssertEqual(verify.bodyJSON?["code"] as? String, "123456")
    }

    func testSocialLoginRoutesToProviderEndpoint() async throws {
        StubServer.shared.install { _ in StubResponse(json: Fixtures.tokenJSON()) }
        let env = TestEnvironment(session: nil)
        let repo = DefaultAuthRepository(client: env.client, tokenStore: env.tokens,
                                         deviceProvider: StaticDeviceInfoProvider(info: DeviceInfo(deviceId: Fixtures.deviceId)))
        _ = try await repo.signIn(with: SocialCredential(idToken: "tok", nonce: "raw-nonce", givenName: "Ana"),
                                  provider: .apple, consents: nil, locale: "pt-BR", timezone: nil)
        let request = try XCTUnwrap(StubServer.shared.requests.first)
        XCTAssertEqual(request.path, "/v1/auth/apple")
        XCTAssertEqual(request.bodyJSON?["id_token"] as? String, "tok")
        XCTAssertEqual(request.bodyJSON?["nonce"] as? String, "raw-nonce")
        XCTAssertEqual(request.bodyJSON?["given_name"] as? String, "Ana")
        XCTAssertNil(request.bodyJSON?["consents"])
    }

    func testLogoutClearsTokensEvenWhenOffline() async throws {
        StubServer.shared.install { _ in throw URLError(.notConnectedToInternet) }
        let env = TestEnvironment()
        let repo = DefaultAuthRepository(client: env.client, tokenStore: env.tokens,
                                         deviceProvider: StaticDeviceInfoProvider(info: DeviceInfo(deviceId: Fixtures.deviceId)))
        await repo.logout()
        XCTAssertNil(try env.tokens.load())
    }

    // MARK: - Bebês

    func testRefreshBabiesCachesAndFallsBackToCacheOffline() async throws {
        let env = TestEnvironment()
        let cache = InMemoryBabyCache()
        let repo = DefaultBabyRepository(client: env.client, cache: cache)

        StubServer.shared.install { _ in StubResponse(json: "{\"items\":[\(Fixtures.babyJSON)]}") }
        let online = try await repo.refreshBabies()
        XCTAssertFalse(online.isFromCache)
        XCTAssertEqual(online.babies.count, 1)

        StubServer.shared.install { _ in throw URLError(.notConnectedToInternet) }
        let offline = try await repo.refreshBabies()
        XCTAssertTrue(offline.isFromCache)
        XCTAssertEqual(offline.babies.first?.displayName, "Nina")
    }

    func testOfflineWithEmptyCacheThrows() async {
        StubServer.shared.install { _ in throw URLError(.notConnectedToInternet) }
        let repo = DefaultBabyRepository(client: TestEnvironment().client, cache: InMemoryBabyCache())
        do {
            _ = try await repo.refreshBabies()
            XCTFail("deveria falhar")
        } catch {
            XCTAssertTrue((error as? APIError)?.isOffline == true)
        }
    }

    func testServerListReplacesCacheSoRevokedBabyDisappears() async throws {
        let env = TestEnvironment()
        let cache = InMemoryBabyCache(babies: [Fixtures.baby()])
        let repo = DefaultBabyRepository(client: env.client, cache: cache)
        StubServer.shared.install { _ in StubResponse(json: "{\"items\":[]}") }
        _ = try await repo.refreshBabies()
        let cached = await repo.cachedBabies()
        XCTAssertTrue(cached.isEmpty)
    }

    func testCreateBabySendsIdempotencyKeyAndCaches() async throws {
        StubServer.shared.install { _ in StubResponse(status: 201, json: Fixtures.babyJSON) }
        let env = TestEnvironment()
        let cache = InMemoryBabyCache()
        let repo = DefaultBabyRepository(client: env.client, cache: cache)
        let key = UUID()
        let create = BabyCreate(id: Fixtures.babyId, displayName: "Nina", birthDate: CivilDate(string: "2026-01-10")!)
        let baby = try await repo.createBaby(create, idempotencyKey: key)
        XCTAssertEqual(baby.id, Fixtures.babyId)
        let request = try XCTUnwrap(StubServer.shared.requests.first)
        XCTAssertEqual(request.path, "/v1/babies")
        XCTAssertEqual(request.header("Idempotency-Key"), key.uuidString.lowercased())
        XCTAssertEqual(request.bodyJSON?["birth_date"] as? String, "2026-01-10")
        let cached = try await cache.loadAll()
        XCTAssertEqual(cached.count, 1)
    }

    func testUpdateBabyUsesMergePatchAndIfMatchWithVersion() async throws {
        StubServer.shared.install { _ in StubResponse(json: Fixtures.babyJSON) }
        let env = TestEnvironment()
        let repo = DefaultBabyRepository(client: env.client, cache: InMemoryBabyCache())
        _ = try await repo.updateBaby(Fixtures.baby(version: 3), patch: BabyUpdate(displayName: "Nini", dueDate: .clear))
        let request = try XCTUnwrap(StubServer.shared.requests.first)
        XCTAssertEqual(request.method, "PATCH")
        XCTAssertEqual(request.path, "/v1/babies/\(Fixtures.babyId.uuidString.lowercased())")
        XCTAssertEqual(request.header("Content-Type"), "application/merge-patch+json")
        XCTAssertEqual(request.header("If-Match"), "\"3\"")
        XCTAssertEqual(request.bodyJSON?["display_name"] as? String, "Nini")
        XCTAssertTrue(request.bodyJSON?["due_date"] is NSNull)
    }

    func testUpdateBabyForbiddenForNonOwnerSurfacesRoleError() async {
        StubServer.shared.install { _ in .problem(403, code: "FORBIDDEN_ROLE") }
        let repo = DefaultBabyRepository(client: TestEnvironment().client, cache: InMemoryBabyCache())
        do {
            _ = try await repo.updateBaby(Fixtures.baby(role: .caregiver), patch: BabyUpdate(displayName: "X"))
            XCTFail("deveria falhar")
        } catch {
            XCTAssertEqual((error as? APIError)?.problemCode, "FORBIDDEN_ROLE")
        }
    }

    // MARK: - Cuidadores

    func testCaregiverEndpoints() async throws {
        let env = TestEnvironment()
        let repo = DefaultCaregiverRepository(client: env.client)
        let membershipId = UUID(uuidString: "11111111-1111-1111-1111-111111111111")!

        StubServer.shared.install { _ in StubResponse(json: "{\"items\":[\(Fixtures.membershipJSON)]}") }
        let list = try await repo.caregivers(babyId: Fixtures.babyId)
        XCTAssertEqual(list.first?.status, .pending)
        XCTAssertEqual(StubServer.shared.requests.last?.path, "/v1/babies/\(Fixtures.babyId.uuidString.lowercased())/caregivers")

        StubServer.shared.install { _ in StubResponse(status: 201, json: Fixtures.membershipJSON) }
        let key = UUID()
        _ = try await repo.invite(babyId: Fixtures.babyId, email: "avo@example.org", role: .readOnly, idempotencyKey: key)
        var request = try XCTUnwrap(StubServer.shared.requests.last)
        XCTAssertEqual(request.path.hasSuffix("/invitations"), true)
        XCTAssertEqual(request.bodyJSON?["role"] as? String, "READ_ONLY")
        XCTAssertEqual(request.header("Idempotency-Key"), key.uuidString.lowercased())

        StubServer.shared.install { _ in StubResponse(json: Fixtures.membershipJSON) }
        _ = try await repo.resendInvitation(babyId: Fixtures.babyId, membershipId: membershipId)
        request = try XCTUnwrap(StubServer.shared.requests.last)
        XCTAssertTrue(request.path.hasSuffix("/invitations/11111111-1111-1111-1111-111111111111/resend"))

        StubServer.shared.install { _ in StubResponse(status: 204) }
        try await repo.remove(babyId: Fixtures.babyId, membershipId: membershipId)
        request = try XCTUnwrap(StubServer.shared.requests.last)
        XCTAssertEqual(request.method, "DELETE")
        XCTAssertTrue(request.path.hasSuffix("/caregivers/11111111-1111-1111-1111-111111111111"))
    }

    func testInvitationInspectAcceptDecline() async throws {
        let env = TestEnvironment()
        let repo = DefaultCaregiverRepository(client: env.client)

        StubServer.shared.install { _ in
            StubResponse(json: "{\"inviter_display_name\":\"Ana\",\"baby_label\":\"N.\",\"role\":\"CAREGIVER\",\"expires_at\":\"2026-10-15T17:00:00Z\",\"visible_data\":[\"timeline\"]}")
        }
        let preview = try await repo.inspectInvitation(token: "tok123")
        XCTAssertEqual(preview.babyLabel, "N.")
        XCTAssertEqual(preview.role, .caregiver)
        XCTAssertEqual(StubServer.shared.requests.last?.bodyJSON?["token"] as? String, "tok123")

        StubServer.shared.install { _ in StubResponse(json: Fixtures.babyJSON) }
        let baby = try await repo.acceptInvitation(token: "tok123")
        XCTAssertEqual(baby.myRole, .owner)

        StubServer.shared.install { _ in StubResponse(status: 204) }
        try await repo.declineInvitation(token: "tok123")
        XCTAssertEqual(StubServer.shared.requests.last?.path, "/v1/invitations/decline")
    }

    func testInspectUnknownRoleInPreviewIsTolerated() async throws {
        StubServer.shared.install { _ in
            StubResponse(json: "{\"inviter_display_name\":\"Ana\",\"baby_label\":\"N.\",\"role\":\"PEDIATRICIAN\",\"expires_at\":\"2026-10-15T17:00:00Z\"}")
        }
        let preview = try await DefaultCaregiverRepository(client: TestEnvironment().client).inspectInvitation(token: "t")
        XCTAssertEqual(preview.role, .unknown("PEDIATRICIAN"))
    }

    // MARK: - Consentimentos

    func testLegalDocumentsAreFetchedWithoutAuth() async throws {
        StubServer.shared.install { _ in
            StubResponse(json: """
            {"items":[{"purpose_key":"TERMS_OF_USE","version":"1.0.0","url":"https://nina.app/termos","effective_at":"2026-10-01T00:00:00Z","required":true},
                      {"purpose_key":"SOME_FUTURE_PURPOSE","version":"0.1","url":"https://nina.app/x","effective_at":"2026-10-01T00:00:00Z"}]}
            """)
        }
        let docs = try await DefaultConsentRepository(client: TestEnvironment(session: nil).client).legalDocuments()
        XCTAssertEqual(docs.count, 2)
        XCTAssertEqual(docs[1].purposeKey, .unknown("SOME_FUTURE_PURPOSE"))
        XCTAssertNil(StubServer.shared.requests.first?.header("Authorization"))
    }
}
