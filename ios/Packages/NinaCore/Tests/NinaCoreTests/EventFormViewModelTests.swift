import XCTest
@testable import NinaCore

@MainActor
final class EventFormViewModelTests: XCTestCase {
    private func make(role: Role = .owner, store: TrackingStore = InMemoryTrackingStore())
        -> (TrackingViewModel, TestClock) {
        let (vm, _, clock) = TrackingFixtures.makeTracking(role: role, store: store)
        return (vm, clock)
    }

    func testBottleDefaultsToLastVolumeAndMilkType() async {
        let (tracking, clock) = make()
        await tracking.reload()
        await tracking.log(.bottle(start: clock.now.addingTimeInterval(-3600), end: nil, volumeMl: 150, milkType: .breastMilk, notes: nil))
        let form = EventFormViewModel(mode: .create(.bottle), tracking: tracking)
        XCTAssertEqual(form.volumeMl, 150)
        XCTAssertEqual(form.milkType, .breastMilk)
        XCTAssertEqual(form.startAt, clock.now)
        XCTAssertFalse(form.hasEnd, "fim é opcional na mamadeira")
        form.adjustVolume(by: 10)
        XCTAssertEqual(form.volumeMl, 160)
        form.adjustVolume(by: -1_000)
        XCTAssertEqual(form.volumeMl, 1, "nunca abaixo de 1 ml")
    }

    func testBottleDefaultVolumeWithoutHistory() async {
        let (tracking, _) = make()
        await tracking.reload()
        XCTAssertEqual(EventFormViewModel(mode: .create(.bottle), tracking: tracking).volumeMl, 120)
    }

    func testMilkTypeFieldOnlyAppearsForBottle() async {
        let (tracking, _) = make()
        await tracking.reload()
        XCTAssertTrue(EventFormViewModel(mode: .create(.bottle), tracking: tracking).showsMilkType)
        for kind in [EventFormViewModel.Kind.breastfeeding, .pumping, .diaper, .sleep] {
            XCTAssertFalse(EventFormViewModel(mode: .create(kind), tracking: tracking).showsMilkType, "\(kind)")
        }
        XCTAssertTrue(EventFormViewModel(mode: .create(.breastfeeding), tracking: tracking).showsSide)
        XCTAssertFalse(EventFormViewModel(mode: .create(.bottle), tracking: tracking).showsSide)
    }

    func testBreastfeedingRequiresSideAndSavesWithDuration() async {
        let store = InMemoryTrackingStore()
        let (tracking, clock) = make(store: store)
        await tracking.reload()
        let form = EventFormViewModel(mode: .create(.breastfeeding), tracking: tracking)
        form.side = nil
        XCTAssertFalse(form.endIsOptional)
        let savedWithoutSide = await form.save()
        XCTAssertFalse(savedWithoutSide)
        XCTAssertEqual(form.fieldErrors["side"]?.key, "validation.required")
        form.side = .left
        form.durationMinutes = 12
        XCTAssertEqual(form.endAt, form.startAt.addingTimeInterval(12 * 60))
        let saved = await form.save()
        XCTAssertTrue(saved)
        let event = form.savedEvent
        XCTAssertEqual(event?.feedingType, .breastfeeding)
        XCTAssertEqual(event?.side, .left)
        XCTAssertEqual(event?.duration, TimeInterval(12 * 60))
        XCTAssertNotNil(event?.endAt, "end_at obrigatório (ADR-0010)")
        _ = clock
    }

    func testMovingStartKeepsDurationForBreastfeeding() async {
        let (tracking, _) = make()
        await tracking.reload()
        let form = EventFormViewModel(mode: .create(.breastfeeding), tracking: tracking)
        form.durationMinutes = 15
        form.moveStart(to: form.startAt.addingTimeInterval(-3600))
        XCTAssertEqual(form.durationMinutes, 15)
    }

    func testSleepManualFormValidatesEndAfterStartAndUsesManualSource() async {
        let store = InMemoryTrackingStore()
        let (tracking, _) = make(store: store)
        await tracking.reload()
        let form = EventFormViewModel(mode: .create(.sleep), tracking: tracking)
        form.endAt = form.startAt.addingTimeInterval(-60)
        let savedWrong = await form.save()
        XCTAssertFalse(savedWrong)
        XCTAssertEqual(form.fieldErrors["endAt"]?.key, "validation.end_before_start")
        form.endAt = form.startAt.addingTimeInterval(40 * 60)
        form.methodOrPlace = "  colo  "
        let saved = await form.save()
        XCTAssertTrue(saved)
        XCTAssertEqual(form.savedEvent?.sleepSource, .manual, "retroativo sem timer = manual (RF-009-A7)")
        XCTAssertEqual(form.savedEvent?.methodOrPlace, "colo")
        let created = await store.allMutations().first
        XCTAssertEqual(created?.data?["source"]?.stringValue, "MANUAL")
    }

    func testPumpingVolumeIsOptional() async {
        let (tracking, _) = make()
        await tracking.reload()
        let form = EventFormViewModel(mode: .create(.pumping), tracking: tracking)
        XCTAssertFalse(form.includesVolume)
        let saved = await form.save()
        XCTAssertTrue(saved)
        XCTAssertNil(form.savedEvent?.volumeMl, "sem volume = nulo, nunca 0")
        let second = EventFormViewModel(mode: .create(.pumping), tracking: tracking)
        second.includesVolume = true
        second.volumeMl = 90
        second.side = .both
        _ = await second.save()
        XCTAssertEqual(second.savedEvent?.volumeMl, 90)
    }

    func testDiaperFormUsesEnumAndLastType() async {
        let (tracking, _) = make()
        await tracking.reload()
        await tracking.logDiaper(.mixed)
        let form = EventFormViewModel(mode: .create(.diaper), tracking: tracking)
        XCTAssertEqual(form.diaperType, .mixed)
        XCTAssertFalse(form.showsEnd)
        form.diaperType = .dry
        _ = await form.save()
        XCTAssertEqual(form.savedEvent?.diaperType, .dry)
    }

    func testEditSendsOnlyChangedFields() async {
        let synced = TrackingFixtures.event(kind: .feeding, start: TrackingFixtures.now.addingTimeInterval(-3600), version: 4)
        let store = InMemoryTrackingStore(events: [synced])
        let (tracking, _) = make(store: store)
        await tracking.reload()
        let form = EventFormViewModel(mode: .edit(synced), tracking: tracking)
        XCTAssertEqual(form.volumeMl, 100)
        form.volumeMl = 130
        let saved = await form.save()
        XCTAssertTrue(saved)
        let mutation = await store.allMutations().last
        XCTAssertEqual(mutation?.op, .update)
        XCTAssertEqual(mutation?.baseVersion, 4)
        XCTAssertEqual(mutation?.data?.objectKeys, ["volume_ml"])
    }

    func testEditWithoutChangesDoesNotEnqueue() async {
        let synced = TrackingFixtures.event(kind: .diaper, start: TrackingFixtures.now.addingTimeInterval(-60), version: 1)
        let store = InMemoryTrackingStore(events: [synced])
        let (tracking, _) = make(store: store)
        await tracking.reload()
        let form = EventFormViewModel(mode: .edit(synced), tracking: tracking)
        let saved = await form.save()
        XCTAssertTrue(saved)
        let mutations = await store.allMutations()
        XCTAssertTrue(mutations.isEmpty)
    }

    func testEditClearingNotesSendsNull() async {
        var synced = TrackingFixtures.event(kind: .diaper, start: TrackingFixtures.now.addingTimeInterval(-60), version: 1)
        synced.notes = "obs"
        let store = InMemoryTrackingStore(events: [synced])
        let (tracking, _) = make(store: store)
        await tracking.reload()
        let form = EventFormViewModel(mode: .edit(synced), tracking: tracking)
        form.notes = "   "
        _ = await form.save()
        let mutation = await store.allMutations().last
        XCTAssertEqual(mutation?.data?["notes"], .null)
    }

    func testReadOnlyCannotSaveForms() async {
        let (tracking, _) = make(role: .readOnly)
        await tracking.reload()
        let form = EventFormViewModel(mode: .create(.diaper), tracking: tracking)
        XCTAssertFalse(form.canSave)
        let saved = await form.save()
        XCTAssertFalse(saved)
        XCTAssertEqual(form.banner, UserMessage("error.event_forbidden"))
    }

    func testOtherFeedingEditKeepsMilkTypeHidden() async {
        let (tracking, _) = make()
        await tracking.reload()
        var solid = TrackingFixtures.event(kind: .feeding, start: TrackingFixtures.now.addingTimeInterval(-60))
        solid.feedingType = .solid
        solid.volumeMl = nil
        let form = EventFormViewModel(mode: .edit(solid), tracking: tracking)
        XCTAssertFalse(form.showsMilkType)
        XCTAssertFalse(form.showsVolume)
    }
}
