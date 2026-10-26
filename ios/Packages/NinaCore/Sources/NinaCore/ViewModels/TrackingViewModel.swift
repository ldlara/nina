import Foundation
import Observation

/// Desfazer de uma ação de registro (UX princípio 7: 8 s).
public struct UndoAction: Equatable, Sendable {
    public enum Kind: Equatable, Sendable {
        case created(UUID)
        case deleted(UUID)
        /// Registro que nunca saiu do aparelho foi descartado; desfazer o recria (mesmo id).
        case discarded(TrackedEvent)
        /// Reabre um sono parado por engano (limpa `end_at`).
        case stoppedSleep(UUID)
    }

    public var kind: Kind
    public var message: UserMessage
    public var expiresAt: Date
}

/// Fonte única da verdade do tracking do bebê selecionado (Hoje + Linha do tempo + timer de sono).
/// Offline-first: todas as ações gravam local primeiro (via `EventRepository`) e depois recarregam o estado;
/// nenhuma depende de rede. Relógio injetado; fuso do bebê para dias e totais.
@Observable
@MainActor
public final class TrackingViewModel {
    public static let undoWindow: TimeInterval = 8
    /// "O bebê ainda está dormindo?" depois de 6 h de timer (UX 4.2; configurável).
    public static let longSleepThreshold: TimeInterval = 6 * 3600
    public static let recentDays = 7

    public let context: TrackingContext
    public private(set) var openSleep: TrackedEvent?
    /// Eventos dos últimos `recentDays` dias (mais recentes primeiro): base de sugestões e do cartão "Agora".
    public private(set) var recentEvents: [TrackedEvent] = []
    public private(set) var selectedDay: CivilDate
    /// Eventos do dia selecionado na timeline (sem filtro), mais recentes primeiro.
    public private(set) var dayEvents: [TrackedEvent] = []
    public var filter: TimelineFilter = .all
    public private(set) var queue: QueueStatus = .empty
    public private(set) var state: LoadState = .idle
    public private(set) var banner: UserMessage?
    public private(set) var undo: UndoAction?
    public private(set) var dismissedLongSleepPrompt = false

    // Alimentados pelo app (rede). O stub de sync nunca altera isso.
    public var isOnline = true
    public var isSyncing = false
    public var failingSince: Date?

    @ObservationIgnored private let repository: EventRepository
    @ObservationIgnored private let now: NowProvider
    @ObservationIgnored private let sleepRule: SleepTypeRule
    @ObservationIgnored private let sync: SyncEngine
    @ObservationIgnored private let autoSync: Bool

    /// `sync` é o motor de rede (Onda 5). Por padrão, o stub: nada é enviado e tudo fica "aguardando envio".
    public init(baby: Baby, repository: EventRepository, now: @escaping NowProvider = NinaClock.system,
                sleepRule: SleepTypeRule = SleepTypeRule(), sync: SyncEngine = StubSyncEngine(),
                autoSync: Bool = true) {
        self.context = TrackingContext(baby: baby)
        self.repository = repository
        self.now = now
        self.sleepRule = sleepRule
        self.sync = sync
        self.autoSync = autoSync
        let tz = TimeZone(identifier: baby.timezone) ?? .current
        self.selectedDay = DayCalendar.day(of: now(), in: tz)
    }

    // MARK: Tempo

    public var babyTimeZone: TimeZone { TimeZone(identifier: context.timeZone) ?? .current }
    public var currentTime: Date { now() }
    public var today: CivilDate { DayCalendar.day(of: now(), in: babyTimeZone) }
    public var canWrite: Bool { context.role.canWriteEvents }
    public var isSelectedDayToday: Bool { selectedDay == today }

    // MARK: Derivados

    public var todayEvents: [TrackedEvent] {
        let tz = babyTimeZone
        let today = self.today
        return recentEvents.filter { DayCalendar.dayKey(for: $0, fallback: tz) == today }
    }

    public var todaySummary: DaySummary {
        var events = todayEvents
        if let open = openSleep, !events.contains(where: { $0.id == open.id }) { events.append(open) }
        return DaySummary.make(day: today, events: events)
    }

    public var selectedDaySummary: DaySummary { DaySummary.make(day: selectedDay, events: dayEvents) }

    /// Dia selecionado, já filtrado pelos chips e em ordem decrescente.
    public var visibleDayEvents: [TrackedEvent] { dayEvents.filter { filter.includes($0) && $0.kind != .wake } }

    /// Última sessão de sono fechada.
    public var lastClosedSleep: TrackedEvent? {
        recentEvents.first { $0.kind == .sleep && $0.endAt != nil }
    }

    /// Acordado desde (fim do último sono fechado), só quando não há sono em andamento.
    public var awakeSince: Date? {
        guard openSleep == nil else { return nil }
        return lastClosedSleep?.endAt
    }

    public var lastFeeding: TrackedEvent? { recentEvents.first { $0.kind == .feeding } }
    public var lastDiaper: TrackedEvent? { recentEvents.first { $0.kind == .diaper } }
    public var lastUsed: LastUsedValues { LastUsedValues(events: recentEvents) }
    /// Ordem dos botões da ação rápida: por uso recente (UX 4.3).
    public var quickActionOrder: [QuickAction] { QuickAction.order(from: recentEvents) }

    /// Nunca houve registro (estado vazio da Home, UX 4.12).
    public var hasNoRecords: Bool { recentEvents.isEmpty && openSleep == nil }

    public func elapsedSleep(at date: Date? = nil) -> TimeInterval? {
        guard let open = openSleep else { return nil }
        return max(0, (date ?? now()).timeIntervalSince(open.startAt))
    }

    public var shouldAskIfStillSleeping: Bool {
        guard !dismissedLongSleepPrompt, let elapsed = elapsedSleep() else { return false }
        return elapsed >= Self.longSleepThreshold
    }

    public func dismissLongSleepPrompt() { dismissedLongSleepPrompt = true }

    public var syncIndicator: SyncIndicator {
        SyncIndicator.resolve(isOnline: isOnline, isSyncing: isSyncing, failingSince: failingSince,
                              pendingCount: queue.pending, rejectedCount: queue.rejected)
    }

    // MARK: Carga

    public func reload() async {
        if state == .idle { state = .loading }
        do {
            let tz = babyTimeZone
            let from = DayCalendar.interval(of: DayCalendar.adding(days: -Self.recentDays, to: today, in: tz), in: tz).lowerBound
            recentEvents = try await repository.events(babyId: context.babyId, from: from, to: nil)
                .filter { $0.kind != .wake }
            openSleep = try await repository.openSleep(babyId: context.babyId)
            queue = try await repository.queueStatus(babyId: nil)
            try await loadSelectedDay()
            state = .loaded
        } catch {
            state = .failed(UserMessage("error.local_storage"))
        }
        if let undo, undo.expiresAt <= now() { self.undo = nil }
    }

    public func selectDay(_ day: CivilDate) async {
        // Não há dia futuro na timeline (nada foi registrado ainda).
        selectedDay = min(day, today)
        do { try await loadSelectedDay() } catch { state = .failed(UserMessage("error.local_storage")) }
    }

    public func shiftSelectedDay(by days: Int) async {
        await selectDay(DayCalendar.adding(days: days, to: selectedDay, in: babyTimeZone))
    }

    private func loadSelectedDay() async throws {
        let tz = babyTimeZone
        let interval = DayCalendar.interval(of: selectedDay, in: tz)
        // Janela ampliada em 1 dia para cada lado: o dia do evento segue o fuso registrado nele (RF-010-A8).
        let from = interval.lowerBound.addingTimeInterval(-86_400)
        let to = interval.upperBound.addingTimeInterval(86_400)
        let day = selectedDay
        dayEvents = try await repository.events(babyId: context.babyId, from: from, to: to)
            .filter { $0.kind != .wake && DayCalendar.dayKey(for: $0, fallback: tz) == day }
    }

    public func wakeEvents(for sleep: TrackedEvent) async -> [TrackedEvent] {
        (try? await repository.wakeEvents(sleepSessionId: sleep.id)) ?? []
    }

    public func clearBanner() { banner = nil }

    // MARK: Timer de sono (RF-009)

    /// Inicia o timer. `minutesAgo > 0` (atalhos -5/-10/-15/-30) ou `startAt` (hora escolhida) é o "Foi antes…"
    /// retroativo. Já existe um aberto: recusa e avisa (o app mostra o existente).
    @discardableResult
    public func startSleep(minutesAgo: Int = 0, startAt: Date? = nil, type: SleepType? = nil) async -> TrackedEvent? {
        let current = now()
        let start = startAt ?? current.addingTimeInterval(-Double(max(0, minutesAgo)) * 60)
        let sleepType = type ?? sleepRule.infer(start: start, in: babyTimeZone)
        return await write(undoMessage: "undo.sleep_started") { [self] in
            let event = try await repository.create(
                .sleep(type: sleepType, start: start, end: nil, source: .timer, methodOrPlace: nil, notes: nil),
                context: context)
            return (event, .created(event.id))
        }
    }

    /// Para o timer. `minutesAgo > 0` ou `endAt` é "Foi antes…" para o fim.
    @discardableResult
    public func stopSleep(minutesAgo: Int = 0, endAt: Date? = nil) async -> TrackedEvent? {
        guard let open = openSleep else {
            banner = UserMessage("error.no_open_sleep")
            return nil
        }
        let end = endAt ?? now().addingTimeInterval(-Double(max(0, minutesAgo)) * 60)
        var changes = EventChanges()
        changes.endAt = .set(end)
        return await write(undoMessage: "undo.sleep_stopped") { [self] in
            let event = try await repository.update(eventId: open.id, changes: changes, context: context)
            return (event, .stoppedSleep(event.id))
        }
    }

    /// Corrige o início do timer em andamento ("Foi antes…" na tela do timer: -5, -10, -15, -30 min).
    @discardableResult
    public func moveOpenSleepStart(earlierByMinutes minutes: Int) async -> TrackedEvent? {
        guard let open = openSleep else { return nil }
        var changes = EventChanges()
        changes.startAt = open.startAt.addingTimeInterval(-Double(minutes) * 60)
        return await write(undoMessage: nil) { [self] in
            (try await repository.update(eventId: open.id, changes: changes, context: context), nil)
        }
    }

    @discardableResult
    public func setOpenSleepType(_ type: SleepType) async -> TrackedEvent? {
        guard let open = openSleep, open.sleepType != type else { return openSleep }
        var changes = EventChanges()
        changes.sleepType = type
        return await write(undoMessage: nil) { [self] in
            (try await repository.update(eventId: open.id, changes: changes, context: context), nil)
        }
    }

    /// "Cancelar registro" do timer (com confirmação na UI).
    public func cancelOpenSleep() async {
        guard let open = openSleep else { return }
        await delete(open)
    }

    // MARK: Registros

    @discardableResult
    public func log(_ draft: EventDraft, undoMessage: String = "undo.saved") async -> TrackedEvent? {
        await write(undoMessage: undoMessage) { [self] in
            let event = try await repository.create(draft, context: context)
            return (event, .created(event.id))
        }
    }

    /// Fralda em um toque: tipo + agora (UX 2.1).
    @discardableResult
    public func logDiaper(_ type: DiaperType, at date: Date? = nil) async -> TrackedEvent? {
        await log(.diaper(occurredAt: date ?? now(), type: type, notes: nil))
    }

    @discardableResult
    public func edit(_ event: TrackedEvent, changes: EventChanges) async -> TrackedEvent? {
        await write(undoMessage: nil) { [self] in
            (try await repository.update(eventId: event.id, changes: changes, context: context), nil)
        }
    }

    public func delete(_ event: TrackedEvent) async {
        let id = event.id
        _ = await write(undoMessage: "undo.deleted") { [self] in
            let outcome = try await repository.delete(eventId: id, context: context)
            let kind: UndoAction.Kind = outcome == .discarded ? .discarded(event) : .deleted(id)
            return (event, kind)
        }
    }

    public func performUndo() async {
        guard let undo, undo.expiresAt > now() else {
            self.undo = nil
            return
        }
        self.undo = nil
        do {
            switch undo.kind {
            case .created(let id):
                _ = try await repository.delete(eventId: id, context: context)
            case .deleted(let id):
                try await repository.undoDelete(eventId: id)
            case .discarded(let event):
                try await repository.restore(event, context: context)
            case .stoppedSleep(let id):
                var changes = EventChanges()
                changes.endAt = .clear
                _ = try await repository.update(eventId: id, changes: changes, context: context)
            }
        } catch {
            banner = Self.message(for: error)
        }
        await reload()
    }

    public func dismissUndo() { undo = nil }

    // MARK: Fila

    /// "Tentar agora": zera esperas de backoff e pede ao motor de sync para rodar.
    public func retryNow() async {
        try? await repository.retryPendingNow(babyId: context.babyId)
        await runSync()
        await reload()
    }

    /// Roda o motor de sync e reflete o resultado no indicador. Com o stub (`notImplemented`) nada muda.
    public func runSync() async {
        guard !isSyncing else { return }
        isSyncing = true
        let outcome = await sync.syncNow(babyId: context.babyId)
        isSyncing = false
        switch outcome {
        case .completed:
            failingSince = nil
        case .failed:
            if failingSince == nil { failingSince = now() }
        case .offline, .notImplemented:
            break
        }
    }

    public func discardRejected() async {
        try? await repository.discardRejected(babyId: context.babyId)
        await reload()
    }

    // MARK: Interno

    private func write(undoMessage: String?, _ work: () async throws -> (TrackedEvent, UndoAction.Kind?)) async -> TrackedEvent? {
        guard canWrite else {
            banner = UserMessage("error.event_forbidden")
            return nil
        }
        do {
            let (event, undoKind) = try await work()
            banner = nil
            if let undoMessage, let undoKind {
                undo = UndoAction(kind: undoKind, message: UserMessage(undoMessage),
                                  expiresAt: now().addingTimeInterval(Self.undoWindow))
            } else {
                undo = nil
            }
            if event.kind == .sleep, event.endAt != nil { dismissedLongSleepPrompt = false }
            await reload()
            // Dispara o envio sem esperar (o registro já está salvo localmente).
            if autoSync { Task { [weak self] in await self?.runSync() } }
            return event
        } catch let error as EventError {
            if case .sleepAlreadyOpen = error { await reload() }
            banner = Self.message(for: error)
            return nil
        } catch {
            banner = Self.message(for: error)
            return nil
        }
    }

    static func message(for error: Error) -> UserMessage {
        guard let error = error as? EventError else { return UserMessage("error.local_storage") }
        switch error {
        case .forbidden: return UserMessage("error.event_forbidden")
        case .sleepAlreadyOpen: return UserMessage("error.sleep_already_open")
        case .noOpenSleep: return UserMessage("error.no_open_sleep")
        case .cannotUndo: return UserMessage("error.cannot_undo")
        case .notFound: return UserMessage("error.event_not_found")
        case .validation(let issues): return issues.first?.message ?? UserMessage("validation.invalid")
        }
    }
}
