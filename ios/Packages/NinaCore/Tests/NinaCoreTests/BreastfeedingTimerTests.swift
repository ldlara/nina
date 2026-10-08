import XCTest
@testable import NinaCore

@MainActor
final class BreastfeedingTimerTests: XCTestCase {
    private func make(store: TrackingStore = InMemoryTrackingStore(), timers: FeedingTimerStore = InMemoryFeedingTimerStore())
        -> (BreastfeedingTimerViewModel, TrackingViewModel, TestClock) {
        let (tracking, _, clock) = TrackingFixtures.makeTracking(store: store)
        return (BreastfeedingTimerViewModel(tracking: tracking, store: timers), tracking, clock)
    }

    func testStartSwitchAndFinishSavesOneSessionPerSideWithEndAt() async {
        let store = InMemoryTrackingStore()
        let (timer, tracking, clock) = make(store: store)
        await tracking.reload()
        timer.start(side: .left)
        XCTAssertTrue(timer.isRunning)
        clock.advance(9 * 60)
        timer.switchSide()
        XCTAssertEqual(timer.currentSide, .right)
        clock.advance(7 * 60)
        XCTAssertEqual(timer.elapsed(at: clock.now), TimeInterval(16 * 60))
        let done = await timer.finish()
        XCTAssertTrue(done)
        XCTAssertNil(timer.timer)

        let feedings = tracking.todayEvents.filter { $0.kind == .feeding }.sorted { $0.startAt < $1.startAt }
        XCTAssertEqual(feedings.map(\.side), [.left, .right])
        XCTAssertEqual(feedings.map(\.duration), [TimeInterval(9 * 60), TimeInterval(7 * 60)])
        XCTAssertTrue(feedings.allSatisfy { $0.endAt != nil && $0.feedingType == .breastfeeding })
        let creates = await store.allMutations().filter { $0.op == .create }
        XCTAssertEqual(creates.count, 2)
        XCTAssertTrue(creates.allSatisfy { $0.data?["end_at"]?.stringValue != nil })
    }

    func testTimerSurvivesAppRestart() {
        let timers = InMemoryFeedingTimerStore()
        let (first, _, clock) = make(timers: timers)
        first.start(side: .right)
        clock.advance(120)
        let (second, _, _) = TrackingFixtures.makeTracking()
        let reopened = BreastfeedingTimerViewModel(tracking: second, store: timers)
        XCTAssertTrue(reopened.isRunning)
        XCTAssertEqual(reopened.currentSide, .right)
    }

    func testDiscardRemovesTimerWithoutSaving() async {
        let store = InMemoryTrackingStore()
        let (timer, tracking, _) = make(store: store)
        await tracking.reload()
        timer.start(side: .left)
        timer.discard()
        XCTAssertFalse(timer.isRunning)
        let mutations = await store.allMutations()
        XCTAssertTrue(mutations.isEmpty)
    }

    func testFinishRightAwayDoesNotSaveEmptyFeeding() async {
        let (timer, tracking, _) = make()
        await tracking.reload()
        timer.start(side: .left)
        let done = await timer.finish()
        XCTAssertFalse(done)
        XCTAssertFalse(timer.isRunning)
        XCTAssertTrue(tracking.todayEvents.isEmpty)
    }

    func testReadOnlyCannotStartTimer() {
        let (tracking, _, _) = TrackingFixtures.makeTracking(role: .readOnly)
        let timer = BreastfeedingTimerViewModel(tracking: tracking, store: InMemoryFeedingTimerStore())
        timer.start(side: .left)
        XCTAssertFalse(timer.isRunning)
        XCTAssertEqual(timer.banner, UserMessage("error.event_forbidden"))
    }

    func testOppositeSide() {
        XCTAssertEqual(BreastSide.left.opposite, .right)
        XCTAssertEqual(BreastSide.right.opposite, .left)
        XCTAssertEqual(BreastSide.both.opposite, .both)
    }
}
