import XCTest
import NinaCore
@testable import Nina

final class EventFormattingTests: XCTestCase {
    func testClockFormatting() {
        XCTAssertEqual(EventFormatting.clock(0), "0:00:00")
        XCTAssertEqual(EventFormatting.clock(42 * 60 + 17), "0:42:17")
        XCTAssertEqual(EventFormatting.clock(3 * 3600 + 5), "3:00:05")
        XCTAssertEqual(EventFormatting.clock(-5), "0:00:00")
    }

    func testTimeIsFormattedInTheGivenTimeZone() {
        // 15:00 UTC = 12:00 em São Paulo; o texto traz "12" no fuso do bebê e "15" em UTC (qualquer locale).
        let instant = Date(timeIntervalSince1970: 1_791_471_600)
        let sp = EventFormatting.time(instant, timeZone: TimeZone(identifier: "America/Sao_Paulo")!)
        let utc = EventFormatting.time(instant, timeZone: TimeZone(identifier: "UTC")!)
        XCTAssertTrue(sp.contains("12"), sp)
        XCTAssertTrue(utc.contains("3") || utc.contains("15"), utc)
        XCTAssertNotEqual(sp, utc)
    }

    func testDayTitleTodayYesterdayAndOtherDay() {
        let tz = TimeZone(identifier: "America/Sao_Paulo")!
        let today = CivilDate(string: "2026-10-08")!
        XCTAssertEqual(EventFormatting.dayTitle(today, today: today, timeZone: tz), NSLocalizedString("day.today", comment: ""))
        XCTAssertEqual(EventFormatting.dayTitle(CivilDate(string: "2026-10-07")!, today: today, timeZone: tz),
                       NSLocalizedString("day.yesterday", comment: ""))
        XCTAssertFalse(EventFormatting.dayTitle(CivilDate(string: "2026-10-01")!, today: today, timeZone: tz).isEmpty)
    }

    func testTitlesForEachKindUseFieldsThatApply() {
        let base = TrackedEvent(id: UUID(), babyId: UUID(), kind: .feeding, tz: "UTC", startAt: Date(), feedingType: .bottle,
                                volumeMl: 120, milkType: .formula, createdAt: Date(), updatedAt: Date())
        XCTAssertTrue(EventFormatting.title(for: base).contains("120"))
        var breast = base
        breast.feedingType = .breastfeeding
        breast.volumeMl = nil
        breast.side = .left
        XCTAssertTrue(EventFormatting.title(for: breast).contains(EventFormatting.name(BreastSide.left)))
        var diaper = base
        diaper.kind = .diaper
        diaper.diaperType = .dirty
        XCTAssertTrue(EventFormatting.title(for: diaper).contains(EventFormatting.name(DiaperType.dirty)))
    }
}
