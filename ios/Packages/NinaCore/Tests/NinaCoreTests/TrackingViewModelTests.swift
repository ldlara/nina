import XCTest
@testable import NinaCore

@MainActor
final class TrackingViewModelTests: XCTestCase {
    private func make(role: Role = .owner, store: TrackingStore = InMemoryTrackingStore())
        -> (TrackingViewModel, DefaultEventRepository, TestClock) {
        TrackingFixtures.makeTracking(role: role, store: store)
    }

    // MARK: Timer de sono (RF-009)

    func testStartAndStopSleepOfflineKeepsOnlyLocalQueueEntries() async throws {
        let store = InMemoryTrackingStore()
        let (vm, _, clock) = make(store: store)
        await vm.reload()
        XCTAssertNil(vm.openSleep)

        let started = await vm.startSleep()
        XCTAssertEqual(started?.sleepSource, .timer)
        XCTAssertEqual(vm.openSleep?.id, started?.id)
        XCTAssertEqual(vm.queue.pending, 1)

        clock.advance(42 * 60)
        XCTAssertEqual(vm.elapsedSleep(), TimeInterval(42 * 60))
        let stopped = await vm.stopSleep()
        XCTAssertEqual(stopped?.duration, TimeInterval(42 * 60))
        XCTAssertNil(vm.openSleep)
        XCTAssertEqual(vm.queue.pending, 2)
        let ops = await store.allMutations().map(\.op)
        XCTAssertEqual(ops, [.create, .update])
    }

    func testTimerSurvivesRestartBecauseItIsDerivedFromPersistedStart() async throws {
        let store = InMemoryTrackingStore()
        let (first, _, clock) = make(store: store)
        await first.reload()
        await first.startSleep()
        clock.advance(3 * 3600)
        // "Reabre o app": novo ViewModel sobre o mesmo banco.
        let (second, _, _) = TrackingFixtures.makeTracking(store: store, clock: clock)
        await second.reload()
        XCTAssertEqual(second.elapsedSleep(), TimeInterval(3 * 3600))
    }

    func testRetroactiveStartAndEndUseWentBackMinutes() async {
        let (vm, _, clock) = make()
        await vm.reload()
        let start = clock.now.addingTimeInterval(-15 * 60)
        await vm.startSleep(minutesAgo: 15)
        XCTAssertEqual(vm.openSleep?.startAt, start)
        clock.advance(60 * 60)
        await vm.stopSleep(minutesAgo: 10)
        XCTAssertEqual(vm.lastClosedSleep?.endAt, clock.now.addingTimeInterval(-10 * 60))
    }

    func testMovingOpenSleepStartEarlier() async {
        let (vm, _, clock) = make()
        await vm.reload()
        await vm.startSleep()
        let original = vm.openSleep!.startAt
        await vm.moveOpenSleepStart(earlierByMinutes: 10)
        XCTAssertEqual(vm.openSleep?.startAt, original.addingTimeInterval(-600))
        _ = clock
    }

    func testStartingSecondTimerShowsBannerAndKeepsTheExisting() async {
        let (vm, _, _) = make()
        await vm.reload()
        let first = await vm.startSleep()
        let second = await vm.startSleep()
        XCTAssertNil(second)
        XCTAssertEqual(vm.banner, UserMessage("error.sleep_already_open"))
        XCTAssertEqual(vm.openSleep?.id, first?.id)
    }

    func testStoppingWithoutTimerReportsError() async {
        let (vm, _, _) = make()
        await vm.reload()
        let result = await vm.stopSleep()
        XCTAssertNil(result)
        XCTAssertEqual(vm.banner, UserMessage("error.no_open_sleep"))
    }

    func testLongRunningSleepAsksOnceAfterSixHours() async {
        let (vm, _, clock) = make()
        await vm.reload()
        await vm.startSleep()
        XCTAssertFalse(vm.shouldAskIfStillSleeping)
        clock.advance(6 * 3600)
        XCTAssertTrue(vm.shouldAskIfStillSleeping)
        vm.dismissLongSleepPrompt()
        XCTAssertFalse(vm.shouldAskIfStillSleeping)
    }

    func testCancelOpenSleepDiscardsItWithoutLeavingAMutation() async {
        let store = InMemoryTrackingStore()
        let (vm, _, _) = make(store: store)
        await vm.reload()
        await vm.startSleep()
        await vm.cancelOpenSleep()
        XCTAssertNil(vm.openSleep)
        let mutations = await store.allMutations()
        XCTAssertTrue(mutations.isEmpty)
    }

    func testInferredSleepTypeFollowsLocalHour() async {
        let (vm, _, clock) = make()
        await vm.reload()
        clock.set(TrackingFixtures.now.addingTimeInterval(9 * 3600)) // 21:00 em São Paulo
        let night = await vm.startSleep()
        XCTAssertEqual(night?.sleepType, .night)
    }

    // MARK: Resumo do dia (RF-010)

    func testDaySummaryTotalsNapsAndIgnoresOpenSleep() async {
        let (vm, repo, clock) = make()
        let ctx = TrackingFixtures.context()
        let base = clock.now.addingTimeInterval(-6 * 3600)
        for (offset, minutes) in [(0, 45), (2, 60), (4, 30)] {
            let start = base.addingTimeInterval(Double(offset) * 3600)
            _ = try? await repo.create(.sleep(type: .nap, start: start, end: start.addingTimeInterval(Double(minutes) * 60),
                                              source: .manual, methodOrPlace: nil, notes: nil), context: ctx)
        }
        _ = try? await repo.create(.diaper(occurredAt: clock.now.addingTimeInterval(-600), type: .wet, notes: nil), context: ctx)
        _ = try? await repo.create(.bottle(start: clock.now.addingTimeInterval(-300), end: nil, volumeMl: 120, milkType: nil, notes: nil), context: ctx)
        _ = try? await repo.create(.breastfeeding(side: .left, start: clock.now.addingTimeInterval(-3000), end: clock.now.addingTimeInterval(-2400), notes: nil), context: ctx)
        await vm.startSleep()
        await vm.reload()
        let summary = vm.todaySummary
        XCTAssertEqual(summary.sleepSeconds, 135 * 60, "45 + 60 + 30 min; o timer aberto fica de fora")
        XCTAssertEqual(summary.napCount, 3)
        XCTAssertTrue(summary.hasOpenSleep)
        XCTAssertEqual(summary.feedingCount, 2)
        XCTAssertEqual(summary.bottleVolumeMl, 120)
        XCTAssertEqual(summary.diaperCount, 1)
    }

    func testAwakeSinceIsEndOfLastSleepAndDisappearsWhileSleeping() async {
        let (vm, repo, clock) = make()
        let ctx = TrackingFixtures.context()
        let start = clock.now.addingTimeInterval(-7200)
        _ = try? await repo.create(.sleep(type: .nap, start: start, end: start.addingTimeInterval(3600), source: .manual,
                                          methodOrPlace: nil, notes: nil), context: ctx)
        await vm.reload()
        XCTAssertEqual(vm.awakeSince, start.addingTimeInterval(3600))
        await vm.startSleep()
        XCTAssertNil(vm.awakeSince)
    }

    func testEmptyStateWhenNothingWasRecorded() async {
        let (vm, _, _) = make()
        await vm.reload()
        XCTAssertTrue(vm.hasNoRecords)
        XCTAssertTrue(vm.todaySummary.isEmpty)
        XCTAssertEqual(vm.state, .loaded)
        await vm.logDiaper(.wet)
        XCTAssertFalse(vm.hasNoRecords)
    }

    // MARK: Fraldas em um toque, sugestões

    func testQuickDiaperAndLastUsedSuggestions() async {
        let (vm, _, clock) = make()
        await vm.reload()
        await vm.logDiaper(.dirty)
        clock.advance(60)
        await vm.log(.bottle(start: clock.now, end: nil, volumeMl: 140, milkType: .formula, notes: nil))
        XCTAssertEqual(vm.lastUsed.diaperType, .dirty)
        XCTAssertEqual(vm.lastUsed.bottleVolumeMl, 140)
        XCTAssertEqual(vm.lastUsed.milkType, .formula)
    }

    // MARK: Desfazer (8 s)

    func testUndoCreateWithinWindowDiscardsRecord() async {
        let store = InMemoryTrackingStore()
        let (vm, _, clock) = make(store: store)
        await vm.reload()
        await vm.logDiaper(.wet)
        XCTAssertEqual(vm.undo?.message, UserMessage("undo.saved"))
        clock.advance(5)
        await vm.performUndo()
        XCTAssertEqual(vm.todaySummary.diaperCount, 0)
        let mutations = await store.allMutations()
        XCTAssertTrue(mutations.isEmpty)
    }

    func testUndoAfterWindowDoesNothing() async {
        let (vm, _, clock) = make()
        await vm.reload()
        await vm.logDiaper(.wet)
        clock.advance(9)
        await vm.performUndo()
        XCTAssertEqual(vm.todaySummary.diaperCount, 1)
        XCTAssertNil(vm.undo)
    }

    func testDeleteThenUndoRestores() async {
        let synced = TrackingFixtures.event(kind: .diaper, start: TrackingFixtures.now.addingTimeInterval(-600), version: 2)
        let (vm, _, _) = make(store: InMemoryTrackingStore(events: [synced]))
        await vm.reload()
        XCTAssertEqual(vm.todaySummary.diaperCount, 1)
        await vm.delete(synced)
        XCTAssertEqual(vm.todaySummary.diaperCount, 0)
        XCTAssertEqual(vm.queue.pending, 1)
        await vm.performUndo()
        XCTAssertEqual(vm.todaySummary.diaperCount, 1)
        XCTAssertEqual(vm.queue.pending, 0)
    }

    func testUndoOfDiscardedUnsentRecordRecreatesItWithSameId() async {
        let store = InMemoryTrackingStore()
        let (vm, _, _) = make(store: store)
        await vm.reload()
        let created = await vm.logDiaper(.wet)
        await vm.delete(created!)
        XCTAssertEqual(vm.todaySummary.diaperCount, 0)
        XCTAssertEqual(vm.undo?.message, UserMessage("undo.deleted"))
        await vm.performUndo()
        XCTAssertEqual(vm.todaySummary.diaperCount, 1)
        XCTAssertEqual(vm.todayEvents.first?.id, created?.id)
        let mutations = await store.allMutations()
        XCTAssertEqual(mutations.map(\.op), [.create])
    }

    func testUndoStopReopensTheTimer() async {
        let (vm, _, clock) = make()
        await vm.reload()
        await vm.startSleep()
        clock.advance(600)
        await vm.stopSleep()
        XCTAssertNil(vm.openSleep)
        await vm.performUndo()
        XCTAssertNotNil(vm.openSleep)
    }

    // MARK: Papéis

    func testReadOnlyCannotRecordAndUIFlagsIt() async {
        let (vm, _, _) = make(role: .readOnly)
        await vm.reload()
        XCTAssertFalse(vm.canWrite)
        let result = await vm.startSleep()
        XCTAssertNil(result)
        XCTAssertEqual(vm.banner, UserMessage("error.event_forbidden"))
        XCTAssertEqual(vm.queue.pending, 0)
    }

    // MARK: Timeline por dia

    func testDayNavigationFiltersAndEmptyDay() async {
        let (vm, repo, clock) = make()
        let ctx = TrackingFixtures.context()
        _ = try? await repo.create(.diaper(occurredAt: clock.now.addingTimeInterval(-86_400), type: .wet, notes: nil), context: ctx)
        _ = try? await repo.create(.diaper(occurredAt: clock.now.addingTimeInterval(-60), type: .wet, notes: nil), context: ctx)
        _ = try? await repo.create(.pumping(start: clock.now.addingTimeInterval(-1200), end: clock.now.addingTimeInterval(-600), volumeMl: nil, side: nil), context: ctx)
        await vm.reload()
        XCTAssertEqual(vm.dayEvents.count, 2)
        vm.filter = .pumping
        XCTAssertEqual(vm.visibleDayEvents.map(\.kind), [.pumping])
        vm.filter = .all
        await vm.shiftSelectedDay(by: -1)
        XCTAssertEqual(vm.dayEvents.count, 1)
        XCTAssertEqual(vm.selectedDaySummary.diaperCount, 1)
        await vm.shiftSelectedDay(by: -1)
        XCTAssertTrue(vm.dayEvents.isEmpty, "dia sem eventos mostra estado vazio")
        await vm.selectDay(CivilDate(string: "2030-01-01")!)
        XCTAssertEqual(vm.selectedDay, vm.today, "não há dia futuro")
    }

    func testTodayUsesBabyTimeZoneNotUTC() async {
        let (vm, _, clock) = make()
        // 2026-10-09 01:30 UTC ainda é dia 8 em São Paulo.
        clock.set(Date(timeIntervalSince1970: 1_791_509_400))
        XCTAssertEqual(vm.today.description, "2026-10-08")
    }

    // MARK: Indicador de sync

    func testSyncIndicatorPhases() {
        let since = TrackingFixtures.now
        XCTAssertEqual(SyncIndicator.resolve(isOnline: true, isSyncing: false, failingSince: nil, pendingCount: 0, rejectedCount: 0).phase, .synced)
        XCTAssertEqual(SyncIndicator.resolve(isOnline: true, isSyncing: false, failingSince: nil, pendingCount: 2, rejectedCount: 0).phase, .pending)
        XCTAssertEqual(SyncIndicator.resolve(isOnline: false, isSyncing: false, failingSince: nil, pendingCount: 2, rejectedCount: 0).phase, .offline)
        XCTAssertEqual(SyncIndicator.resolve(isOnline: true, isSyncing: true, failingSince: nil, pendingCount: 2, rejectedCount: 0).phase, .syncing)
        XCTAssertEqual(SyncIndicator.resolve(isOnline: true, isSyncing: false, failingSince: since, pendingCount: 2, rejectedCount: 0).phase, .error(since: since))
    }

    func testStubSyncKeepsEverythingPendingAndNeverClaimsSuccess() async {
        let (vm, _, _) = make()
        await vm.reload()
        await vm.logDiaper(.wet)
        await vm.retryNow()
        XCTAssertEqual(vm.queue.pending, 1)
        XCTAssertEqual(vm.syncIndicator.phase, .pending)
        XCTAssertFalse(vm.isSyncing)
        XCTAssertNil(vm.failingSince)
    }

    func testOfflineIndicatorKeepsPendingCount() async {
        let (vm, _, _) = make()
        await vm.reload()
        await vm.logDiaper(.wet)
        vm.isOnline = false
        XCTAssertEqual(vm.syncIndicator, SyncIndicator(phase: .offline, pendingCount: 1, rejectedCount: 0))
    }

    func testQuickActionsAreOrderedByRecentUse() async {
        let (vm, _, clock) = make()
        await vm.reload()
        XCTAssertEqual(vm.quickActionOrder, [.sleep, .breastfeeding, .bottle, .diaper, .pumping])
        await vm.logDiaper(.wet, at: clock.now.addingTimeInterval(-600))
        await vm.log(.bottle(start: clock.now.addingTimeInterval(-60), end: nil, volumeMl: 90, milkType: nil, notes: nil))
        XCTAssertEqual(Array(vm.quickActionOrder.prefix(2)), [.bottle, .diaper])
    }

    func testRetroactiveSleepWithExactTimeChosenByTheUser() async {
        let (vm, _, clock) = make()
        await vm.reload()
        let chosen = clock.now.addingTimeInterval(-47 * 60)
        await vm.startSleep(startAt: chosen)
        XCTAssertEqual(vm.openSleep?.startAt, chosen)
        clock.advance(30 * 60)
        let end = clock.now.addingTimeInterval(-5 * 60)
        await vm.stopSleep(endAt: end)
        XCTAssertEqual(vm.lastClosedSleep?.endAt, end)
    }

    func testSyncOutcomeStub() async {
        let outcome = await StubSyncEngine().syncNow(babyId: nil)
        XCTAssertEqual(outcome, .notImplemented)
    }
}
