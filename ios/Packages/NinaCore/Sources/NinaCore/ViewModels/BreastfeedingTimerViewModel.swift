import Foundation
import Observation

public extension BreastSide {
    /// Lado para "Trocar de lado". `BOTH` e valores desconhecidos não trocam.
    var opposite: BreastSide {
        switch self {
        case .left: return .right
        case .right: return .left
        default: return self
        }
    }
}

public struct BreastfeedingSegment: Codable, Equatable, Sendable {
    public var side: BreastSide
    public var startedAt: Date
    public var endedAt: Date?

    public init(side: BreastSide, startedAt: Date, endedAt: Date? = nil) {
        self.side = side
        self.startedAt = startedAt
        self.endedAt = endedAt
    }
}

/// Timer de mamada em andamento. Vive **só no cliente** até terminar (ADR-0010, decisão 5: `end_at` obrigatório,
/// então não existe mamada "aberta" no servidor). É guardado no aparelho para sobreviver ao app fechado.
public struct BreastfeedingTimerState: Codable, Equatable, Sendable {
    public var babyId: UUID
    public var segments: [BreastfeedingSegment]

    public init(babyId: UUID, segments: [BreastfeedingSegment]) {
        self.babyId = babyId
        self.segments = segments
    }

    public var current: BreastfeedingSegment? {
        guard let last = segments.last, last.endedAt == nil else { return nil }
        return last
    }
}

public protocol FeedingTimerStore: Sendable {
    func load(babyId: UUID) -> BreastfeedingTimerState?
    func save(_ state: BreastfeedingTimerState)
    func clear(babyId: UUID)
}

public final class InMemoryFeedingTimerStore: FeedingTimerStore, @unchecked Sendable {
    private let lock = NSLock()
    private var states: [UUID: BreastfeedingTimerState] = [:]

    public init() {}

    public func load(babyId: UUID) -> BreastfeedingTimerState? {
        lock.lock(); defer { lock.unlock() }
        return states[babyId]
    }

    public func save(_ state: BreastfeedingTimerState) {
        lock.lock(); defer { lock.unlock() }
        states[state.babyId] = state
    }

    public func clear(babyId: UUID) {
        lock.lock(); defer { lock.unlock() }
        states[babyId] = nil
    }
}

/// Mamada no peito com timer: escolhe o lado (inicia), "Trocar de lado" (1 toque), "Terminar" (salva).
/// Cada lado vira uma sessão de alimentação própria com `end_at` (a duração por lado é preservada).
@Observable
@MainActor
public final class BreastfeedingTimerViewModel {
    public private(set) var timer: BreastfeedingTimerState?
    public private(set) var banner: UserMessage?
    public private(set) var isFinishing = false

    @ObservationIgnored private let tracking: TrackingViewModel
    @ObservationIgnored private let store: FeedingTimerStore

    public init(tracking: TrackingViewModel, store: FeedingTimerStore) {
        self.tracking = tracking
        self.store = store
        self.timer = store.load(babyId: tracking.context.babyId)
    }

    public var isRunning: Bool { timer?.current != nil }
    public var currentSide: BreastSide? { timer?.current?.side }

    /// Tempo total desde o primeiro segmento, somando os lados.
    public func elapsed(at date: Date) -> TimeInterval {
        guard let timer else { return 0 }
        return timer.segments.reduce(0) { total, segment in
            total + max(0, (segment.endedAt ?? date).timeIntervalSince(segment.startedAt))
        }
    }

    public func elapsedCurrentSide(at date: Date) -> TimeInterval {
        guard let current = timer?.current else { return 0 }
        return max(0, date.timeIntervalSince(current.startedAt))
    }

    public func start(side: BreastSide) {
        guard tracking.canWrite else {
            banner = UserMessage("error.event_forbidden")
            return
        }
        guard timer?.current == nil else { return }
        let segment = BreastfeedingSegment(side: side, startedAt: tracking.currentTime.roundedToSecond)
        persist(BreastfeedingTimerState(babyId: tracking.context.babyId, segments: [segment]))
        banner = nil
    }

    public func switchSide() {
        guard var state = timer, var last = state.segments.last, last.endedAt == nil else { return }
        let now = tracking.currentTime.roundedToSecond
        last.endedAt = now
        state.segments[state.segments.count - 1] = last
        state.segments.append(BreastfeedingSegment(side: last.side.opposite, startedAt: now))
        persist(state)
    }

    public func discard() {
        store.clear(babyId: tracking.context.babyId)
        timer = nil
        banner = nil
    }

    /// Fecha o lado atual e grava uma sessão por lado. Segmentos de menos de 1 s são ignorados.
    /// Devolve `true` se tudo foi salvo (a UI fecha o timer).
    @discardableResult
    public func finish() async -> Bool {
        guard var state = timer else { return false }
        isFinishing = true
        defer { isFinishing = false }
        let now = tracking.currentTime.roundedToSecond
        if var last = state.segments.last, last.endedAt == nil {
            last.endedAt = now
            state.segments[state.segments.count - 1] = last
        }
        var remaining = state.segments.filter { ($0.endedAt ?? $0.startedAt).timeIntervalSince($0.startedAt) >= 1 }
        guard !remaining.isEmpty else {
            discard()
            banner = UserMessage("validation.end_before_start")
            return false
        }
        while let segment = remaining.first, let end = segment.endedAt {
            let saved = await tracking.log(.breastfeeding(side: segment.side, start: segment.startedAt, end: end, notes: nil),
                                           undoMessage: "undo.saved")
            guard saved != nil else {
                // Mantém só o que falta; o que já foi salvo não é duplicado em nova tentativa.
                persist(BreastfeedingTimerState(babyId: state.babyId, segments: remaining))
                banner = tracking.banner ?? UserMessage.generic
                return false
            }
            remaining.removeFirst()
        }
        discard()
        return true
    }

    private func persist(_ state: BreastfeedingTimerState) {
        store.save(state)
        timer = state
    }
}
