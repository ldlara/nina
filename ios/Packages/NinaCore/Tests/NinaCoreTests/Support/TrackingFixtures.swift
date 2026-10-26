import Foundation
@testable import NinaCore

/// Relógio de teste controlável (nada de `Date()` real nos testes de tracking).
final class TestClock: @unchecked Sendable {
    private let lock = NSLock()
    private var current: Date

    init(_ date: Date = TrackingFixtures.now) { current = date }

    var now: Date {
        lock.lock(); defer { lock.unlock() }
        return current
    }

    func advance(_ seconds: TimeInterval) {
        lock.lock(); current = current.addingTimeInterval(seconds); lock.unlock()
    }

    func set(_ date: Date) {
        lock.lock(); current = date; lock.unlock()
    }

    var provider: NowProvider { { [self] in self.now } }
}

/// Gerador de UUIDs previsíveis (1, 2, 3...) para asserções estáveis.
final class SequentialIds: @unchecked Sendable {
    private let lock = NSLock()
    private var counter = 0

    func next() -> UUID {
        lock.lock(); defer { lock.unlock() }
        counter += 1
        return UUID(uuidString: String(format: "00000000-0000-4000-8000-%012ld", counter))!
    }
}

enum TrackingFixtures {
    /// 2026-10-08 15:00:00 UTC = 12:00 em São Paulo (UTC-3).
    static let now = Date(timeIntervalSince1970: 1_791_471_600)
    static let babyId = Fixtures.babyId
    static let deviceId = Fixtures.deviceId
    static let author = UserRef(id: Fixtures.userId, displayName: "Ana")
    static let saoPaulo = TimeZone(identifier: "America/Sao_Paulo")!

    static func context(role: Role = .owner) -> TrackingContext {
        TrackingContext(babyId: babyId, timeZone: "America/Sao_Paulo", role: role)
    }

    static func makeRepository(store: TrackingStore = InMemoryTrackingStore(), clock: TestClock = TestClock(),
                               ids: SequentialIds = SequentialIds(), undoHold: TimeInterval = 8) -> DefaultEventRepository {
        DefaultEventRepository(store: store, deviceId: deviceId, now: clock.provider, makeId: { ids.next() },
                               author: { author }, undoHold: undoHold)
    }

    static func event(kind: EventKind = .diaper, id: UUID = UUID(), start: Date = now, end: Date? = nil,
                      version: Int = 1, status: EventSyncStatus = .synced, babyId: UUID = babyId) -> TrackedEvent {
        var event = TrackedEvent(id: id, babyId: babyId, kind: kind, version: version, tz: "America/Sao_Paulo",
                                 startAt: start, endAt: end, createdAt: start, updatedAt: start, syncStatus: status)
        switch kind {
        case .sleep: event.sleepType = .nap; event.sleepSource = .manual
        case .feeding: event.feedingType = .bottle; event.volumeMl = 100
        case .diaper: event.diaperType = .wet
        case .pumping: event.endAt = end ?? start.addingTimeInterval(600)
        case .wake: event.endAt = end ?? start.addingTimeInterval(300)
        }
        return event
    }

    @MainActor
    static func makeTracking(role: Role = .owner, store: TrackingStore = InMemoryTrackingStore(),
                             clock: TestClock = TestClock()) -> (TrackingViewModel, DefaultEventRepository, TestClock) {
        let repository = makeRepository(store: store, clock: clock)
        let baby = Fixtures.baby(role: role)
        return (TrackingViewModel(baby: baby, repository: repository, now: clock.provider, autoSync: false), repository, clock)
    }
}
