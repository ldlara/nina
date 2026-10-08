import XCTest
@testable import NinaCore

final class SyncWireTests: XCTestCase {
    private let now = TrackingFixtures.now
    private let baby = TrackingFixtures.babyId

    private func build(_ draft: EventDraft) -> TrackedEvent {
        draft.build(id: UUID(), babyId: baby, tz: "America/Sao_Paulo", now: now, author: nil)
    }

    // Chaves permitidas por `additionalProperties: false` em cada *Data do contrato.
    func testCreateDataUsesOnlyKeysAllowedByTheContract() {
        let start = now.addingTimeInterval(-600)
        let cases: [(EventDraft, Set<String>)] = [
            (.sleep(type: .nap, start: start, end: nil, source: .timer, methodOrPlace: "colo", notes: "ok"),
             ["sleep_type", "start_at", "end_at", "tz", "source", "method_or_place", "notes"]),
            (.breastfeeding(side: .left, start: start, end: now, notes: nil),
             ["feeding_type", "side", "start_at", "end_at", "tz"]),
            (.bottle(start: start, end: nil, volumeMl: 120, milkType: .formula, notes: nil),
             ["feeding_type", "start_at", "end_at", "volume_ml", "milk_type", "tz"]),
            (.pumping(start: start, end: now, volumeMl: 80, side: .both),
             ["start_at", "end_at", "volume_ml", "side", "tz"]),
            (.diaper(occurredAt: now, type: .mixed, notes: nil), ["occurred_at", "diaper_type", "tz"]),
            (.wake(sleepSessionId: UUID(), start: start, end: now, source: .manual),
             ["sleep_session_id", "started_at", "ended_at", "source"])
        ]
        for (draft, expected) in cases {
            let data = MutationFactory.createData(for: build(draft))
            XCTAssertEqual(data.objectKeys, expected, "\(draft.kind)")
        }
    }

    func testOpenSleepSendsExplicitNullEndAndBottleMilkTypeNullWhenNotInformed() {
        let sleep = MutationFactory.createData(for: build(.sleep(type: .nap, start: now, end: nil, source: .timer,
                                                                  methodOrPlace: nil, notes: nil)))
        XCTAssertEqual(sleep["end_at"], .null)
        let bottle = MutationFactory.createData(for: build(.bottle(start: now, end: nil, volumeMl: 90, milkType: nil, notes: nil)))
        XCTAssertEqual(bottle["milk_type"], .null)
        XCTAssertEqual(bottle["volume_ml"], .int(90))
        XCTAssertEqual(bottle["feeding_type"]?.stringValue, "BOTTLE")
    }

    func testBreastfeedingNeverCarriesMilkTypeOrVolume() {
        let data = MutationFactory.createData(for: build(.breastfeeding(side: .right, start: now.addingTimeInterval(-60), end: now, notes: nil)))
        XCTAssertNil(data["milk_type"])
        XCTAssertNil(data["volume_ml"])
        XCTAssertEqual(data["side"]?.stringValue, "RIGHT")
        XCTAssertEqual(data["feeding_type"]?.stringValue, "BREASTFEEDING")
    }

    func testUnknownEnumValuesAreSentBackAsReceived() {
        var event = build(.diaper(occurredAt: now, type: .wet, notes: nil))
        event.diaperType = .unknown("FUTURE_TYPE")
        XCTAssertEqual(MutationFactory.createData(for: event)["diaper_type"]?.stringValue, "FUTURE_TYPE")
    }

    func testPatchUsesContractFieldNamesPerKindAndNullToClear() {
        var wake = build(.wake(sleepSessionId: UUID(), start: now.addingTimeInterval(-60), end: now, source: .manual))
        var changes = EventChanges()
        changes.startAt = now.addingTimeInterval(-120)
        changes.endAt = .set(now)
        changes.wakeSource = .manual
        let wakePatch = MutationFactory.patchData(for: wake, changes: changes)
        XCTAssertEqual(wakePatch.objectKeys, ["started_at", "ended_at", "source"])
        wake.kind = .diaper
        let diaperPatch = MutationFactory.patchData(for: wake, changes: changes)
        XCTAssertTrue(diaperPatch.objectKeys.contains("occurred_at"))

        var sleepChanges = EventChanges()
        sleepChanges.endAt = .clear
        sleepChanges.notes = .clear
        let sleep = build(.sleep(type: .nap, start: now, end: now.addingTimeInterval(60), source: .manual, methodOrPlace: nil, notes: "x"))
        let patch = MutationFactory.patchData(for: sleep, changes: sleepChanges)
        XCTAssertEqual(patch["end_at"], .null)
        XCTAssertEqual(patch["notes"], .null)
    }

    func testMilkTypePatchIsDroppedOutsideBottle() {
        let breast = build(.breastfeeding(side: .left, start: now.addingTimeInterval(-60), end: now, notes: nil))
        var changes = EventChanges()
        changes.milkType = .set(.formula)
        changes.notes = .set("a")
        XCTAssertEqual(MutationFactory.patchData(for: breast, changes: changes).objectKeys, ["notes"])
    }

    func testPushBodyMatchesContractEnvelope() throws {
        let entity = UUID(uuidString: "0B8F1A52-6F3E-4A0C-8F86-6C3D7A3C1A10")!
        let m = QueuedMutation(mutationId: UUID(uuidString: "9C1D2E3F-0A1B-4C2D-8E3F-1A2B3C4D5E01")!, op: .create,
                               entityType: .sleepSession, entityId: entity, babyId: baby, baseVersion: 0,
                               clientCreatedAt: Date(timeIntervalSince1970: 1_791_471_602), deviceId: TrackingFixtures.deviceId,
                               data: .object(["sleep_type": .string("NAP")]))
        let del = QueuedMutation(op: .delete, entityType: .sleepSession, entityId: entity, babyId: baby, baseVersion: 1,
                                 clientCreatedAt: now, deviceId: TrackingFixtures.deviceId)
        let body = try SyncWire.pushBody(deviceId: TrackingFixtures.deviceId, mutations: [m, del])
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: body) as? [String: Any])
        XCTAssertEqual(json["device_id"] as? String, TrackingFixtures.deviceId.uuidString.lowercased())
        let items = try XCTUnwrap(json["mutations"] as? [[String: Any]])
        XCTAssertEqual(items.count, 2)
        XCTAssertEqual(items[0]["mutation_id"] as? String, "9c1d2e3f-0a1b-4c2d-8e3f-1a2b3c4d5e01")
        XCTAssertEqual(items[0]["op"] as? String, "CREATE")
        XCTAssertEqual(items[0]["entity_type"] as? String, "SLEEP_SESSION")
        XCTAssertEqual(items[0]["base_version"] as? Int, 0)
        XCTAssertEqual(items[0]["client_created_at"] as? String, NinaJSON.formatInstant(Date(timeIntervalSince1970: 1_791_471_602)))
        XCTAssertNotNil(items[0]["data"])
        XCTAssertNil(items[1]["data"], "DELETE não leva data")
        XCTAssertEqual(items[1]["op"] as? String, "DELETE")
    }

    func testPushResponseFromContractExampleDecodesAndMapsToQueueResults() throws {
        let json = """
        {"server_time":"2026-10-08T18:10:03Z","results":[
          {"mutation_id":"9c1d2e3f-0a1b-4c2d-8e3f-1a2b3c4d5e01","status":"APPLIED","entity_type":"SLEEP_SESSION",
           "entity_id":"0b8f1a52-6f3e-4a0c-8f86-6c3d7a3c1a10","version":1,"resolution":"NONE",
           "server_received_at":"2026-10-08T18:10:03Z"},
          {"mutation_id":"9c1d2e3f-0a1b-4c2d-8e3f-1a2b3c4d5e02","status":"DUPLICATE","entity_type":"SLEEP_SESSION",
           "entity_id":"0b8f1a52-6f3e-4a0c-8f86-6c3d7a3c1a10","version":2,"resolution":"NONE",
           "warnings":[{"code":"CLIENT_CLOCK_SKEW","skew_seconds":90000,"tolerance_seconds":86400}]},
          {"mutation_id":"9c1d2e3f-0a1b-4c2d-8e3f-1a2b3c4d5e04","status":"REJECTED","entity_type":"DIAPER_EVENT",
           "entity_id":"5e2f0b1a-3c4d-4e5f-9a6b-7c8d9e0f1a2b","retryable":false,
           "problem":{"type":"https://api.nina.app/problems/entity-deleted","title":"x","status":409,"code":"ENTITY_DELETED"}},
          {"mutation_id":"9c1d2e3f-0a1b-4c2d-8e3f-1a2b3c4d5e05","status":"SOMETHING_NEW","resolution":"FUTURE_RESOLUTION"}
        ]}
        """
        let response = try NinaJSON.makeDecoder().decode(PushResponse.self, from: Data(json.utf8))
        XCTAssertEqual(response.results.count, 4)
        XCTAssertEqual(response.results[0].status, .applied)
        XCTAssertEqual(response.results[0].version, 1)
        XCTAssertTrue(response.results[1].hasClockSkewWarning)
        XCTAssertEqual(response.results[1].warnings?.first?.skewSeconds, 90000)
        let rejected = response.results[2].itemResult()
        XCTAssertEqual(rejected.problemCode, "ENTITY_DELETED")
        XCTAssertEqual(rejected.retryable, false)
        XCTAssertEqual(response.results[3].status, .unknown("SOMETHING_NEW"), "status futuro não derruba o decode")
        XCTAssertEqual(response.results[3].resolution, .unknown("FUTURE_RESOLUTION"))
    }

    func testTrackedEventRoundTripKeepsUnknownEnumsAndExactTimes() throws {
        var event = build(.bottle(start: now, end: nil, volumeMl: 150, milkType: .unknown("PLANT_BASED"), notes: "n"))
        event.feedingType = .unknown("PUREE")
        event.nightAwakenings = 0
        let data = try NinaStorageJSON.makeEncoder().encode(event)
        let decoded = try NinaStorageJSON.makeDecoder().decode(TrackedEvent.self, from: data)
        XCTAssertEqual(decoded, event)
        XCTAssertEqual(decoded.milkType, .unknown("PLANT_BASED"))
        XCTAssertEqual(decoded.nightAwakenings, 0, "0 (acompanhamento sem despertar) é diferente de nulo")
    }

    func testJSONValueKeepsSnakeCaseKeysThroughStorageCoders() throws {
        let value: JSONValue = .object(["start_at": .string("2026-10-08T17:00:00Z"), "end_at": .null, "volume_ml": .int(5)])
        let data = try NinaStorageJSON.makeEncoder().encode(value)
        let back = try NinaStorageJSON.makeDecoder().decode(JSONValue.self, from: data)
        XCTAssertEqual(back, value)
        XCTAssertEqual(back.objectKeys, ["start_at", "end_at", "volume_ml"])
    }
}
