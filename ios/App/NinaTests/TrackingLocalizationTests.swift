import XCTest
import NinaCore
@testable import Nina

/// Chaves do tracking existem em pt-BR, en e es (paridade total já é coberta por `LocalizationTests`).
final class TrackingLocalizationTests: XCTestCase {
    private let languages = ["pt-BR", "en", "es"]

    private func strings(_ language: String) throws -> [String: String] {
        let path = try XCTUnwrap(Bundle.main.path(forResource: "Localizable", ofType: "strings",
                                                   inDirectory: nil, forLocalization: language))
        return try XCTUnwrap(NSDictionary(contentsOfFile: path) as? [String: String])
    }

    func testValidationCodesEmittedByTheCoreAreLocalized() throws {
        let codes = ["required", "invalid", "timezone_invalid", "future_time", "notes_too_long", "end_before_start",
                     "method_too_long", "not_applicable", "end_required", "volume_required", "volume_range",
                     "wake_outside_sleep"]
        for language in languages {
            let table = try strings(language)
            for code in codes { XCTAssertNotNil(table["validation." + code], "\(language): validation.\(code)") }
        }
    }

    func testTrackingMessagesAndUndoKeysExist() throws {
        let keys = ["error.event_forbidden", "error.sleep_already_open", "error.no_open_sleep", "error.cannot_undo",
                    "error.event_not_found", "error.local_storage", "undo.saved", "undo.deleted", "undo.sleep_started",
                    "undo.sleep_stopped", "tracking.read_only", "sync.offline", "sync.syncing", "sync.all_synced"]
        for language in languages {
            let table = try strings(language)
            for key in keys { XCTAssertNotNil(table[key], "\(language): \(key)") }
        }
    }

    func testEveryEnumValueHasALabel() throws {
        // Nenhum valor conhecido pode cair em "Outro" por esquecimento de tradução.
        let unknown = NSLocalizedString("enum.unknown", comment: "")
        for type in DiaperType.allCases { XCTAssertNotEqual(EventFormatting.name(type), unknown, "\(type)") }
        // `MilkType.other` ("Outro") coincide de propósito com o rótulo genérico.
        for type in MilkType.allCases where type != .other {
            XCTAssertNotEqual(EventFormatting.name(type), unknown, "\(type)")
        }
        for side in BreastSide.allCases { XCTAssertNotEqual(EventFormatting.name(side), unknown, "\(side)") }
        XCTAssertEqual(EventFormatting.name(DiaperType.unknown("NEW")), unknown, "valor futuro vira rótulo genérico, não a chave")
    }

    func testPluralKeysForSyncExistInAllLanguages() throws {
        for language in languages {
            let path = try XCTUnwrap(Bundle.main.path(forResource: "Localizable", ofType: "stringsdict",
                                                       inDirectory: nil, forLocalization: language))
            let dict = try XCTUnwrap(NSDictionary(contentsOfFile: path))
            for key in ["sync.pending_count", "sync.rejected_count", "more.logout.pending_warning"] {
                let entry = try XCTUnwrap(dict[key] as? [String: Any], "\(language): \(key)")
                let count = try XCTUnwrap(entry["count"] as? [String: Any])
                XCTAssertNotNil(count["one"]); XCTAssertNotNil(count["other"])
            }
        }
    }
}
