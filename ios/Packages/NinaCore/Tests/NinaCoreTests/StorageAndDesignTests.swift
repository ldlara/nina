import XCTest
@testable import NinaCore

final class StorageAndDesignTests: XCTestCase {
    func testInMemoryTokenStoreRoundTrip() throws {
        let store = InMemoryTokenStore()
        XCTAssertNil(try store.load())
        let session = Fixtures.session()
        try store.save(session)
        XCTAssertEqual(try store.load(), session)
        try store.clear()
        XCTAssertNil(try store.load())
    }

    #if canImport(Security)
    func testKeychainTokenStoreRoundTrip() throws {
        let store = KeychainTokenStore(service: "app.nina.tests.\(UUID().uuidString)", account: "test")
        defer { try? store.clear() }
        let session = StoredSession(accessToken: "a", accessTokenExpiresAt: Date(timeIntervalSince1970: 1_790_000_000),
                                    refreshToken: "r", refreshTokenExpiresAt: Date(timeIntervalSince1970: 1_792_000_000),
                                    sessionId: UUID(), userId: UUID())
        do {
            XCTAssertNil(try store.load())
            try store.save(session)
            XCTAssertEqual(try store.load(), session)
            var updated = session
            updated.accessToken = "a2"
            try store.save(updated)
            XCTAssertEqual(try store.load()?.accessToken, "a2")
            try store.clear()
            XCTAssertNil(try store.load())
        } catch KeychainError.unexpectedStatus(let status) where status == -34018 {
            throw XCTSkip("Keychain indisponível neste host de testes (errSecMissingEntitlement). Rode no simulador.")
        }
    }
    #endif

    func testInMemoryBabyCacheOrdersByCreation() async throws {
        let older = Fixtures.baby(id: UUID(), name: "A")
        var newer = Fixtures.baby(id: UUID(), name: "B")
        newer.createdAt = older.createdAt.addingTimeInterval(60)
        let cache = InMemoryBabyCache()
        try await cache.replaceAll([newer, older])
        let names = try await cache.loadAll().map(\.displayName)
        XCTAssertEqual(names, ["A", "B"])
        try await cache.remove(id: older.id)
        let remaining = try await cache.loadAll()
        XCTAssertEqual(remaining.count, 1)
    }

    func testBabyJSONRoundTripForCache() throws {
        let baby = try NinaJSON.makeDecoder().decode(Baby.self, from: Data(Fixtures.babyJSON.utf8))
        let data = try NinaJSON.makeEncoder().encode(baby)
        let again = try NinaJSON.makeDecoder().decode(Baby.self, from: data)
        XCTAssertEqual(again.id, baby.id)
        XCTAssertEqual(again.birthDate, baby.birthDate)
        XCTAssertEqual(again.ageCalculation, baby.ageCalculation)
        XCTAssertEqual(again.myRole, baby.myRole)
    }

    // MARK: - Contraste (ux-spec §10.2)

    func testTextTokensMeetAANormalTextOnAllSurfaces() {
        let surfaces: [NinaColorToken] = [.bgApp, .bgSurface, .bgSurfaceRaised]
        let texts: [NinaColorToken] = [.textPrimary, .textSecondary, .accentPrimary]
        for surface in surfaces {
            for text in texts {
                XCTAssertGreaterThanOrEqual(RGB.contrast(text.light, surface.light), 4.5, "claro \(text) em \(surface)")
                XCTAssertGreaterThanOrEqual(RGB.contrast(text.dark, surface.dark), 4.5, "escuro \(text) em \(surface)")
            }
        }
    }

    func testStateAndEventTokensMeetContrast() {
        for token in [NinaColorToken.stateSuccess, .stateWarning, .stateError] {
            for surface in [NinaColorToken.bgApp, .bgSurface] {
                XCTAssertGreaterThanOrEqual(RGB.contrast(token.light, surface.light), 4.5, "\(token) claro")
                XCTAssertGreaterThanOrEqual(RGB.contrast(token.dark, surface.dark), 4.5, "\(token) escuro")
            }
        }
        for token in [NinaColorToken.eventSleep, .eventFeeding, .eventDiaper, .eventPumping, .focusRing] {
            XCTAssertGreaterThanOrEqual(RGB.contrast(token.light, NinaColorToken.bgSurface.light), 3, "\(token) claro")
            XCTAssertGreaterThanOrEqual(RGB.contrast(token.dark, NinaColorToken.bgSurface.dark), 3, "\(token) escuro")
        }
    }

    func testPrimaryButtonTextContrast() {
        let accent = NinaColorToken.accentPrimary
        let onAccent = NinaColorToken.textOnAccent
        XCTAssertGreaterThanOrEqual(RGB.contrast(onAccent.light, accent.light), 4.5)
        XCTAssertGreaterThanOrEqual(RGB.contrast(onAccent.dark, accent.dark), 4.5)
    }

    func testDarkModeNeverUsesPureBlackOrWhite() {
        for token in NinaColorToken.allCases {
            XCTAssertNotEqual(token.dark, RGB(hex: 0x000000), "\(token)")
        }
        XCTAssertNotEqual(NinaColorToken.textPrimary.dark, RGB(hex: 0xFFFFFF))
    }

    func testBorderSubtleIsDecorativeOnly() {
        // 1.3:1 e 1.5:1: serve como divisor, não como borda de campo (UI exige 3:1). Os campos usam textSecondary.
        XCTAssertLessThan(RGB.contrast(NinaColorToken.borderSubtle.light, NinaColorToken.bgApp.light), 3)
        XCTAssertGreaterThanOrEqual(RGB.contrast(NinaColorToken.textSecondary.light, NinaColorToken.bgSurface.light), 3)
        XCTAssertGreaterThanOrEqual(RGB.contrast(NinaColorToken.textSecondary.dark, NinaColorToken.bgSurface.dark), 3)
    }
}
