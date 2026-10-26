import XCTest
@testable import NinaCore

final class ModelDecodingTests: XCTestCase {
    private let decoder = NinaJSON.makeDecoder()
    private let encoder = NinaJSON.makeEncoder()

    private func object(_ encodable: some Encodable) throws -> [String: Any] {
        let data = try encoder.encode(encodable)
        return try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    func testDecodesBabyFromContractExample() throws {
        let baby = try decoder.decode(Baby.self, from: Data(Fixtures.babyJSON.utf8))
        XCTAssertEqual(baby.displayName, "Nina")
        XCTAssertEqual(baby.birthDate.description, "2026-01-10")
        XCTAssertEqual(baby.dueDate?.description, "2026-01-24")
        XCTAssertNil(baby.sex)
        XCTAssertEqual(baby.myRole, .owner)
        XCTAssertEqual(baby.ageCalculation, AgeCalculation(chronologicalDays: 271, correctedDays: 257, correctionApplied: true))
        XCTAssertEqual(baby.age?.displayed, .chronological)
        XCTAssertEqual(baby.version, 3)
    }

    func testNullCorrectedDaysMeansNotApplicableNotZero() throws {
        let json = Fixtures.babyJSON
            .replacingOccurrences(of: "\"corrected\":{\"days\":257,\"weeks\":36,\"months\":8}", with: "\"corrected\":null")
            .replacingOccurrences(of: "\"corrected_days\":257,\"correction_applied\":true", with: "\"corrected_days\":null,\"correction_applied\":false")
        let baby = try decoder.decode(Baby.self, from: Data(json.utf8))
        XCTAssertNil(baby.ageCalculation?.correctedDays)
        let summary = try XCTUnwrap(AgeSummary(baby: baby))
        XCTAssertNil(summary.corrected)
        XCTAssertEqual(summary.chronological.months, 8)
    }

    func testAgeSummaryFallsBackToAgeCalculationDays() {
        var baby = Fixtures.baby()
        baby.ageCalculation = AgeCalculation(chronologicalDays: 90, correctedDays: 76, correctionApplied: true)
        let summary = AgeSummary(baby: baby)
        XCTAssertEqual(summary?.chronological.weeks, 12)
        XCTAssertEqual(summary?.chronological.months, 2)
        XCTAssertEqual(summary?.corrected?.weeks, 10)
    }

    func testUnknownEnumValuesAreToleratedAndPreserved() throws {
        let json = Fixtures.membershipJSON
            .replacingOccurrences(of: "\"CAREGIVER\"", with: "\"GUARDIAN_PLUS\"")
            .replacingOccurrences(of: "\"PENDING\"", with: "\"SUSPENDED\"")
        let membership = try decoder.decode(Membership.self, from: Data(json.utf8))
        XCTAssertEqual(membership.role, .unknown("GUARDIAN_PLUS"))
        XCTAssertEqual(membership.status, .unknown("SUSPENDED"))
        XCTAssertFalse(membership.role.canWriteEvents, "papel desconhecido nunca ganha permissão de escrita")
        // Reencodar preserva o texto original.
        let reencoded = try object(membership)
        XCTAssertEqual(reencoded["role"] as? String, "GUARDIAN_PLUS")
    }

    func testUnknownSexAndAgeDisplayedAreTolerated() throws {
        let json = Fixtures.babyJSON
            .replacingOccurrences(of: "\"sex\":null", with: "\"sex\":\"NON_BINARY\"")
            .replacingOccurrences(of: "\"CHRONOLOGICAL\"", with: "\"HYBRID\"")
        let baby = try decoder.decode(Baby.self, from: Data(json.utf8))
        XCTAssertEqual(baby.sex, .unknown("NON_BINARY"))
        XCTAssertEqual(baby.age?.displayed, .unknown("HYBRID"))
    }

    func testKnownEnumRawValuesAreUppercaseContractValues() {
        XCTAssertEqual(Role.readOnly.rawValue, "READ_ONLY")
        XCTAssertEqual(InvitableRole.caregiver.rawValue, "CAREGIVER")
        XCTAssertEqual(PurposeKey.childDataGuardian.rawValue, "CHILD_DATA_GUARDIAN")
        XCTAssertEqual(DevicePlatform.ios.rawValue, "IOS")
        XCTAssertEqual(UserStatus.pendingDeletion.rawValue, "PENDING_DELETION")
        XCTAssertEqual(Role(rawValue: "OWNER"), .owner)
    }

    func testDecodesTokenResponseAndUser() throws {
        let response = try decoder.decode(TokenResponse.self, from: Data(Fixtures.tokenJSON().utf8))
        XCTAssertEqual(response.tokenType, "Bearer")
        XCTAssertEqual(response.expiresIn, 900)
        XCTAssertEqual(response.user.identities.first?.provider, .apple)
        XCTAssertEqual(response.user.status, .active)
        XCTAssertNil(response.pendingConsents)
    }

    func testRequestsEncodeAsSnakeCase() throws {
        let device = DeviceInfo(deviceId: Fixtures.deviceId, platform: .ios, deviceLabel: "iPhone", appVersion: "1.0.0", osVersion: "17.5")
        let json = try object(LoginRequest(email: "a@b.co", password: "x", device: device))
        let deviceJSON = try XCTUnwrap(json["device"] as? [String: Any])
        XCTAssertEqual(deviceJSON["device_id"] as? String, Fixtures.deviceId.uuidString)
        XCTAssertEqual(deviceJSON["platform"] as? String, "IOS")
        XCTAssertEqual(deviceJSON["app_version"] as? String, "1.0.0")
        XCTAssertEqual(deviceJSON["os_version"] as? String, "17.5")

        let social = try object(SocialLoginRequest(idToken: "t", nonce: "n", device: device, givenName: "Ana"))
        XCTAssertEqual(social["id_token"] as? String, "t")
        XCTAssertEqual(social["given_name"] as? String, "Ana")

        let register = try object(RegisterRequest(email: "a@b.co", password: "p", displayName: nil, locale: "pt-BR",
                                                  timezone: nil,
                                                  consents: [ConsentAcceptance(purposeKey: .termsOfUse, documentVersion: "1.0.0")]))
        let consents = try XCTUnwrap(register["consents"] as? [[String: Any]])
        XCTAssertEqual(consents.first?["purpose_key"] as? String, "TERMS_OF_USE")
        XCTAssertEqual(consents.first?["document_version"] as? String, "1.0.0")
    }

    func testBabyCreateEncodesCivilDatesAndOmitsNils() throws {
        let create = BabyCreate(id: Fixtures.babyId, displayName: "Lia", birthDate: CivilDate(string: "2026-05-02")!,
                                dueDate: CivilDate(string: "2026-05-10"), sex: .female, timezone: "America/Sao_Paulo")
        let json = try object(create)
        XCTAssertEqual(json["birth_date"] as? String, "2026-05-02")
        XCTAssertEqual(json["due_date"] as? String, "2026-05-10")
        XCTAssertEqual(json["sex"] as? String, "FEMALE")
        XCTAssertEqual(json["display_name"] as? String, "Lia")
        let minimal = try object(BabyCreate(displayName: "Lia", birthDate: CivilDate(string: "2026-05-02")!))
        XCTAssertNil(minimal["due_date"])
        XCTAssertNil(minimal["id"])
    }

    func testBabyUpdateMergePatchSemantics() throws {
        let cleared = try object(BabyUpdate(dueDate: .clear, sex: .set(.male)))
        XCTAssertTrue(cleared["due_date"] is NSNull, "clear envia null explícito")
        XCTAssertEqual(cleared["sex"] as? String, "MALE")
        XCTAssertNil(cleared["display_name"], "campo não alterado não é enviado")

        let empty = BabyUpdate()
        XCTAssertTrue(empty.isEmpty)
        XCTAssertEqual(try object(empty).count, 0)
    }

    func testCivilDateValidationAndOrdering() {
        XCTAssertNotNil(CivilDate(string: "2024-02-29"))
        XCTAssertNil(CivilDate(string: "2026-02-29"))
        XCTAssertNil(CivilDate(string: "2026-13-01"))
        XCTAssertNil(CivilDate(string: "26-1-1"))
        XCTAssertNil(CivilDate(string: "2026-01-10T00:00:00Z"))
        XCTAssertLessThan(CivilDate(string: "2026-01-10")!, CivilDate(string: "2026-01-24")!)
        XCTAssertEqual(CivilDate(year: 2026, month: 3, day: 5)?.description, "2026-03-05")
    }

    func testCivilDateFromInstantUsesGivenTimeZone() {
        // 2026-01-10T02:00:00Z ainda é dia 9 em São Paulo (UTC-3).
        let instant = NinaJSON.parseInstant("2026-01-10T02:00:00Z")!
        XCTAssertEqual(CivilDate(date: instant, timeZone: TimeZone(identifier: "America/Sao_Paulo")!).description, "2026-01-09")
        XCTAssertEqual(CivilDate(date: instant, timeZone: TimeZone(identifier: "UTC")!).description, "2026-01-10")
    }

    func testInstantParsingAcceptsFractionalSeconds() {
        XCTAssertNotNil(NinaJSON.parseInstant("2026-10-08T17:10:00.250Z"))
        XCTAssertNotNil(NinaJSON.parseInstant("2026-10-08T17:10:00Z"))
        XCTAssertNil(NinaJSON.parseInstant("08/10/2026"))
    }

    func testProblemDecodingKeepsFieldErrorsAndDefaultsCode() throws {
        let json = """
        {"type":"https://api.nina.app/problems/validation-failed","title":"Validation failed","status":400,
         "code":"VALIDATION_FAILED","request_id":"01J9","errors":[{"field":"data.password","code":"TOO_SHORT"}],
         "retry_after_seconds":30,"some_future_field":{"a":1}}
        """
        let problem = try decoder.decode(Problem.self, from: Data(json.utf8))
        XCTAssertEqual(problem.code, "VALIDATION_FAILED")
        XCTAssertEqual(problem.requestId, "01J9")
        XCTAssertEqual(problem.errors, [FieldError(field: "data.password", code: "TOO_SHORT")])
        XCTAssertEqual(problem.retryAfterSeconds, 30)

        let bare = try decoder.decode(Problem.self, from: Data("{\"status\":500}".utf8))
        XCTAssertEqual(bare.code, "UNKNOWN")
    }

    func testIgnoresUnknownFieldsInResponses() throws {
        let json = Fixtures.babyJSON.replacingOccurrences(of: "\"version\":3,", with: "\"version\":3,\"new_server_field\":[1,2],")
        XCTAssertNoThrow(try decoder.decode(Baby.self, from: Data(json.utf8)))
    }

    func testErrorMapperUsesStableCodesOnly() {
        let known = APIError.problem(Problem(code: "INVALID_CREDENTIALS", status: 401), httpStatus: 401)
        XCTAssertEqual(ErrorMapper.message(for: known).key, "error.invalid_credentials")
        let unknown = APIError.problem(Problem(code: "SOMETHING_NEW", status: 418), httpStatus: 418)
        XCTAssertEqual(ErrorMapper.message(for: unknown), .generic)
        XCTAssertEqual(ErrorMapper.message(for: APIError.network(URLError(.notConnectedToInternet))), .offline)
        let fields = APIError.problem(Problem(code: "VALIDATION_FAILED", status: 400,
                                              errors: [FieldError(field: "data.email", code: "INVALID")]), httpStatus: 400)
        XCTAssertEqual(ErrorMapper.fieldMessages(for: fields)["email"]?.key, "validation.invalid")
    }
}
