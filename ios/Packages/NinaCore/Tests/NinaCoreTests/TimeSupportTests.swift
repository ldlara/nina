import XCTest
@testable import NinaCore

final class TimeSupportTests: XCTestCase {
    private func date(_ seconds: TimeInterval) -> Date { Date(timeIntervalSince1970: seconds) }

    // MARK: DST (dias de 23 h e 25 h)

    func testDayIntervalOnSpringForwardIs23Hours() {
        let ny = TimeZone(identifier: "America/New_York")!
        let interval = DayCalendar.interval(of: CivilDate(string: "2026-03-08")!, in: ny)
        XCTAssertEqual(interval.lowerBound, date(1_772_946_000)) // 2026-03-08T05:00Z (meia-noite EST)
        XCTAssertEqual(interval.upperBound, date(1_773_028_800)) // 2026-03-09T04:00Z (meia-noite EDT)
        XCTAssertEqual(interval.upperBound.timeIntervalSince(interval.lowerBound), 23 * 3600)
    }

    func testDayIntervalOnFallBackIs25Hours() {
        let ny = TimeZone(identifier: "America/New_York")!
        let interval = DayCalendar.interval(of: CivilDate(string: "2026-11-01")!, in: ny)
        XCTAssertEqual(interval.lowerBound, date(1_793_505_600))
        XCTAssertEqual(interval.upperBound, date(1_793_595_600))
        XCTAssertEqual(interval.upperBound.timeIntervalSince(interval.lowerBound), 25 * 3600)
    }

    func testDayStartWhenMidnightDoesNotExist() {
        // Brasil teve horário de verão que começava à meia-noite (2018-11-04: 00:00 -> 01:00).
        let sp = TimeZone(identifier: "America/Sao_Paulo")!
        let interval = DayCalendar.interval(of: CivilDate(string: "2018-11-04")!, in: sp)
        XCTAssertEqual(interval.lowerBound, date(1_541_300_400)) // 03:00Z = 01:00 local
        XCTAssertEqual(interval.upperBound, date(1_541_383_200))
        XCTAssertEqual(DayCalendar.day(of: interval.lowerBound, in: sp).description, "2018-11-04")
        XCTAssertEqual(DayCalendar.day(of: interval.upperBound.addingTimeInterval(-1), in: sp).description, "2018-11-04")
    }

    func testAddingDaysAcrossDSTKeepsCivilDays() {
        let ny = TimeZone(identifier: "America/New_York")!
        let start = CivilDate(string: "2026-03-07")!
        XCTAssertEqual(DayCalendar.adding(days: 1, to: start, in: ny).description, "2026-03-08")
        XCTAssertEqual(DayCalendar.adding(days: 2, to: start, in: ny).description, "2026-03-09")
        XCTAssertEqual(DayCalendar.adding(days: -7, to: CivilDate(string: "2026-03-09")!, in: ny).description, "2026-03-02")
    }

    func testDayKeyUsesTheTimeZoneRecordedOnTheEvent() {
        // 2026-10-09 01:30 UTC: ainda dia 8 em São Paulo (UTC-3), já dia 9 em Lisboa/UTC.
        let instant = date(1_791_509_400)
        var event = TrackingFixtures.event(kind: .diaper, start: instant)
        event.tz = "America/Sao_Paulo"
        XCTAssertEqual(DayCalendar.dayKey(for: event, fallback: TimeZone(identifier: "UTC")!).description, "2026-10-08")
        event.tz = "UTC"
        XCTAssertEqual(DayCalendar.dayKey(for: event, fallback: TrackingFixtures.saoPaulo).description, "2026-10-09")
        event.tz = "Not/AZone"
        XCTAssertEqual(DayCalendar.dayKey(for: event, fallback: TrackingFixtures.saoPaulo).description, "2026-10-08",
                       "fuso inválido cai no fuso do bebê")
    }

    func testNightSleepAcrossMidnightCountsOnTheDayItStarted() {
        // Sono de 21:00 (dia 7) a 05:00 (dia 8) em São Paulo: pertence ao dia 7 (D-13 em aberto: premissa).
        let start = date(1_791_471_600 - 15 * 3600) // 2026-10-07 21:00 local
        let event = TrackingFixtures.event(kind: .sleep, start: start, end: start.addingTimeInterval(8 * 3600), version: 1)
        XCTAssertEqual(DayCalendar.dayKey(for: event, fallback: TrackingFixtures.saoPaulo).description, "2026-10-07")
    }

    // MARK: Tipo de sono sugerido

    func testSleepTypeInferenceByLocalHour() {
        let rule = SleepTypeRule()
        let sp = TrackingFixtures.saoPaulo
        XCTAssertEqual(rule.infer(start: TrackingFixtures.now, in: sp), .nap, "12:00 local")
        XCTAssertEqual(rule.infer(start: TrackingFixtures.now.addingTimeInterval(8 * 3600), in: sp), .night, "20:00 local")
        XCTAssertEqual(rule.infer(start: TrackingFixtures.now.addingTimeInterval(-7 * 3600), in: sp), .night, "05:00 local")
        XCTAssertEqual(rule.infer(start: TrackingFixtures.now.addingTimeInterval(-6 * 3600), in: sp), .nap, "06:00 local")
    }

    func testDurationParts() {
        XCTAssertEqual(DurationParts(seconds: 0).totalMinutes, 0)
        XCTAssertEqual(DurationParts(seconds: 80 * 60 + 59), DurationParts(seconds: 80 * 60))
        XCTAssertEqual(DurationParts(seconds: 80 * 60).hours, 1)
        XCTAssertEqual(DurationParts(seconds: 80 * 60).minutes, 20)
        XCTAssertEqual(DurationParts(seconds: -5).totalMinutes, 0)
    }

    func testRoundingToSecondsMatchesWire() {
        XCTAssertEqual(Date(timeIntervalSince1970: 100.9).roundedToSecond, Date(timeIntervalSince1970: 100))
    }
}
