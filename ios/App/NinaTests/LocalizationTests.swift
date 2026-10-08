import XCTest
import NinaCore
@testable import Nina

/// Garante paridade pt-BR / en / es: mesmas chaves, mesmos marcadores de formato, e as chaves que o
/// NinaCore pode emitir existem em todos os idiomas.
final class LocalizationTests: XCTestCase {
    private let languages = ["pt-BR", "en", "es"]

    private func strings(for language: String) throws -> [String: String] {
        let path = try XCTUnwrap(Bundle.main.path(forResource: "Localizable", ofType: "strings",
                                                   inDirectory: nil, forLocalization: language),
                                 "Localizable.strings ausente para \(language)")
        return try XCTUnwrap(NSDictionary(contentsOfFile: path) as? [String: String])
    }

    func testAllLanguagesHaveTheSameKeys() throws {
        let reference = Set(try strings(for: "pt-BR").keys)
        XCTAssertFalse(reference.isEmpty)
        for language in languages {
            let keys = Set(try strings(for: language).keys)
            XCTAssertEqual(keys.subtracting(reference), [], "chaves a mais em \(language)")
            XCTAssertEqual(reference.subtracting(keys), [], "chaves faltando em \(language)")
        }
    }

    func testFormatSpecifiersMatchAcrossLanguages() throws {
        let reference = try strings(for: "pt-BR")
        let pattern = try NSRegularExpression(pattern: "%(?:\\d+\\$)?(?:lld|@|d)")
        func specifiers(_ text: String) -> [String] {
            pattern.matches(in: text, range: NSRange(text.startIndex..., in: text))
                .compactMap { Range($0.range, in: text).map { String(text[$0]) } }
        }
        for language in languages where language != "pt-BR" {
            let other = try strings(for: language)
            for (key, value) in reference {
                XCTAssertEqual(specifiers(value).count, specifiers(other[key] ?? "").count, "\(language): \(key)")
            }
        }
    }

    func testCoreErrorAndValidationKeysExist() throws {
        let codes = ["VALIDATION_FAILED", "INVALID_CREDENTIALS", "FORBIDDEN_ROLE", "ACCESS_REVOKED", "CONSENT_REQUIRED",
                     "NOT_FOUND", "IDENTITY_LINK_REQUIRED", "ALREADY_MEMBER", "VERSION_CONFLICT", "RATE_LIMITED",
                     "CLIENT_UPGRADE_REQUIRED", "SESSION_REVOKED", "TOKEN_EXPIRED", "REFRESH_TOKEN_REUSED",
                     "IDEMPOTENCY_KEY_REUSE", "OWNER_REQUIRED"]
        let extra = ["error.generic", "error.offline", "error.invitation_invalid", "error.legal_unavailable",
                     "error.social_cancelled", "error.social_failed", "error.social_not_configured",
                     "validation.required", "validation.invalid", "validation.email_invalid",
                     "validation.password_too_short", "validation.terms_required", "validation.name_too_long",
                     "validation.birth_in_future", "validation.guardian_required", "validation.timezone_invalid"]
        let keys = codes.map { "error." + $0.lowercased() } + extra
        for language in languages {
            let table = try strings(for: language)
            for key in keys { XCTAssertNotNil(table[key], "\(language): \(key)") }
        }
    }

    func testPluralDictionariesExistForAgeUnits() throws {
        for language in languages {
            let path = try XCTUnwrap(Bundle.main.path(forResource: "Localizable", ofType: "stringsdict",
                                                       inDirectory: nil, forLocalization: language))
            let dict = try XCTUnwrap(NSDictionary(contentsOfFile: path))
            for key in ["age.months", "age.weeks", "age.days"] { XCTAssertNotNil(dict[key], "\(language): \(key)") }
        }
    }

    func testUnknownServerCodesFallBackToGenericMessages() {
        let unknownValidation = LocalizedMessage.string(for: UserMessage("validation.some_new_code"))
        XCTAssertNotEqual(unknownValidation, "validation.some_new_code", "nunca mostrar a chave crua")
        let unknownError = LocalizedMessage.string(for: UserMessage("error.some_new_code"))
        XCTAssertNotEqual(unknownError, "error.some_new_code")
    }
}
