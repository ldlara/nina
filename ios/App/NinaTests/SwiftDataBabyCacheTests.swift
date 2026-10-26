import XCTest
import SwiftData
import NinaCore
@testable import Nina

final class SwiftDataBabyCacheTests: XCTestCase {
    private func makeCache() -> SwiftDataBabyCache {
        SwiftDataBabyCache(modelContainer: LocalStore.makeContainer(inMemory: true))
    }

    private func baby(_ name: String, created: TimeInterval, id: UUID = UUID()) -> Baby {
        Baby(id: id, displayName: name, birthDate: CivilDate(string: "2026-01-10")!,
             dueDate: CivilDate(string: "2026-01-24"), sex: .unknown("FUTURE_VALUE"), timezone: "America/Sao_Paulo",
             myRole: .unknown("FUTURE_ROLE"), age: nil,
             ageCalculation: AgeCalculation(chronologicalDays: 10, correctedDays: nil, correctionApplied: false),
             version: 2, createdAt: Date(timeIntervalSince1970: created), updatedAt: Date(timeIntervalSince1970: created))
    }

    func testRoundTripPreservesUnknownEnumsAndNullCorrectedAge() async throws {
        let cache = makeCache()
        try await cache.replaceAll([baby("Nina", created: 1_790_000_000)])
        let loaded = try await cache.loadAll()
        XCTAssertEqual(loaded.count, 1)
        XCTAssertEqual(loaded[0].sex, .unknown("FUTURE_VALUE"))
        XCTAssertEqual(loaded[0].myRole, .unknown("FUTURE_ROLE"))
        XCTAssertNil(loaded[0].ageCalculation?.correctedDays)
        XCTAssertEqual(loaded[0].dueDate?.description, "2026-01-24")
    }

    func testReplaceAllDropsBabiesThatLostAccess() async throws {
        let cache = makeCache()
        let keep = baby("Fica", created: 1_790_000_100)
        try await cache.replaceAll([baby("Sai", created: 1_790_000_000), keep])
        try await cache.replaceAll([keep])
        let names = try await cache.loadAll().map(\.displayName)
        XCTAssertEqual(names, ["Fica"])
    }

    func testUpsertUpdatesExistingRowAndOrdersByCreation() async throws {
        let cache = makeCache()
        let id = UUID()
        try await cache.upsert(baby("Segundo", created: 1_790_000_100))
        try await cache.upsert(baby("Primeiro", created: 1_790_000_000, id: id))
        try await cache.upsert(baby("Primeiro (editado)", created: 1_790_000_000, id: id))
        let names = try await cache.loadAll().map(\.displayName)
        XCTAssertEqual(names, ["Primeiro (editado)", "Segundo"])
    }

    func testRemoveAndClear() async throws {
        let cache = makeCache()
        let id = UUID()
        try await cache.replaceAll([baby("A", created: 1_790_000_000, id: id), baby("B", created: 1_790_000_100)])
        try await cache.remove(id: id)
        let remaining = try await cache.loadAll()
        XCTAssertEqual(remaining.map(\.displayName), ["B"])
        try await cache.clear()
        let empty = try await cache.loadAll()
        XCTAssertTrue(empty.isEmpty)
    }
}
