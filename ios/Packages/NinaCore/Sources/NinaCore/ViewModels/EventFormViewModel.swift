import Foundation
import Observation

/// Formulário de criação/edição de eventos (sono manual/retroativo, mamada no peito, mamadeira, bomba, fralda,
/// despertar). Valida localmente com as mesmas regras do repositório e mostra os erros junto ao campo.
@Observable
@MainActor
public final class EventFormViewModel {
    public enum Kind: Equatable, Sendable {
        case sleep, breastfeeding, bottle, pumping, diaper
        /// Alimentação sólida/outra (só edição de itens vindos do servidor).
        case otherFeeding
        case wake(sleepSessionId: UUID)
    }

    public enum Mode: Equatable, Sendable {
        case create(Kind)
        case edit(TrackedEvent)
    }

    public static let defaultBottleVolumeMl = 120
    public static let volumeStepMl = 10

    public let mode: Mode
    public let kind: Kind

    public var startAt: Date
    public var endAt: Date
    /// Sono: `false` mantém o sono em andamento (sem fim). Mamadeira: fim opcional.
    public var hasEnd: Bool
    public var sleepType: SleepType
    public var methodOrPlace = ""
    public var notes = ""
    public var side: BreastSide?
    public var volumeMl: Int
    /// Bomba: volume é opcional.
    public var includesVolume: Bool
    /// `nil` = não informado (só mamadeira).
    public var milkType: MilkType?
    public var diaperType: DiaperType

    public private(set) var fieldErrors: [String: UserMessage] = [:]
    public private(set) var banner: UserMessage?
    public private(set) var isSaving = false
    public private(set) var savedEvent: TrackedEvent?

    @ObservationIgnored private let tracking: TrackingViewModel
    @ObservationIgnored private let original: TrackedEvent?

    public init(mode: Mode, tracking: TrackingViewModel) {
        self.mode = mode
        self.tracking = tracking
        let now = tracking.currentTime.roundedToSecond
        let last = tracking.lastUsed

        // Valores iniciais neutros; os específicos de cada tipo vêm abaixo.
        var start = now
        var end = now
        var hasEnd = true
        var sleepType = SleepType.nap
        var side: BreastSide?
        var volume = Self.defaultBottleVolumeMl
        var includesVolume = true
        var milk: MilkType?
        var diaper = DiaperType.wet
        let resolvedKind: Kind

        switch mode {
        case .create(let kind):
            resolvedKind = kind
            self.original = nil
            switch kind {
            case .sleep:
                start = now.addingTimeInterval(-3600)
                sleepType = SleepTypeRule().infer(start: start, in: tracking.babyTimeZone)
            case .breastfeeding:
                start = now.addingTimeInterval(-10 * 60)
                side = last.breastSide
            case .bottle:
                hasEnd = false
                volume = last.bottleVolumeMl ?? Self.defaultBottleVolumeMl
                milk = last.milkType
            case .pumping:
                start = now.addingTimeInterval(-15 * 60)
                includesVolume = false
            case .diaper:
                diaper = last.diaperType ?? .wet
                hasEnd = false
            case .wake:
                start = now.addingTimeInterval(-10 * 60)
            case .otherFeeding:
                hasEnd = false
            }
        case .edit(let event):
            self.original = event
            start = event.startAt
            end = event.endAt ?? now
            hasEnd = event.endAt != nil
            sleepType = event.sleepType ?? .nap
            side = event.side
            volume = event.volumeMl ?? Self.defaultBottleVolumeMl
            includesVolume = event.volumeMl != nil
            milk = event.milkType
            diaper = event.diaperType ?? .wet
            switch event.kind {
            case .sleep: resolvedKind = .sleep
            case .feeding:
                switch event.feedingType {
                case .some(.bottle): resolvedKind = .bottle
                case .some(.breastfeeding): resolvedKind = .breastfeeding
                default: resolvedKind = .otherFeeding
                }
            case .pumping: resolvedKind = .pumping
            case .diaper: resolvedKind = .diaper
            case .wake: resolvedKind = .wake(sleepSessionId: event.sleepSessionId ?? UUID())
            }
        }
        self.kind = resolvedKind
        self.startAt = start
        self.endAt = end
        self.hasEnd = hasEnd
        self.sleepType = sleepType
        self.side = side
        self.volumeMl = volume
        self.includesVolume = includesVolume
        self.milkType = milk
        self.diaperType = diaper
        if let event = original {
            self.methodOrPlace = event.methodOrPlace ?? ""
            self.notes = event.notes ?? ""
        }
    }

    // MARK: Regras de exibição dos campos

    public var isEditing: Bool { if case .edit = mode { return true } else { return false } }
    public var showsEnd: Bool {
        switch kind {
        case .diaper: return false
        default: return true
        }
    }
    /// Mamada, bomba e despertar exigem fim; sono e mamadeira podem ficar sem (sono em andamento).
    public var endIsOptional: Bool {
        switch kind {
        case .sleep, .bottle, .otherFeeding: return true
        default: return false
        }
    }
    public var showsSide: Bool { kind == .breastfeeding || kind == .pumping }
    public var sideIsRequired: Bool { kind == .breastfeeding }
    public var showsVolume: Bool { kind == .bottle || kind == .pumping }
    public var volumeIsOptional: Bool { kind == .pumping }
    /// `milk_type` só se aplica a mamadeira (BOTTLE).
    public var showsMilkType: Bool { kind == .bottle }
    public var showsDiaperType: Bool { kind == .diaper }
    public var showsSleepType: Bool { kind == .sleep }
    public var showsNotes: Bool {
        switch kind {
        case .pumping, .wake: return false
        default: return true
        }
    }
    public var showsMethodOrPlace: Bool { kind == .sleep }
    public var showsDuration: Bool { kind == .breastfeeding || kind == .pumping || kind == .wake }
    public var canSave: Bool { tracking.canWrite && !isSaving }
    /// Fuso do bebê, usado pelos seletores de data e hora.
    public var timeZone: TimeZone { tracking.babyTimeZone }
    /// Limite superior dos seletores (agora + tolerância de relógio).
    public var maxSelectableTime: Date { tracking.currentTime.addingTimeInterval(EventValidator.futureToleranceSeconds) }
    /// Faixa do seletor de fim (nunca invertida, para o `DatePicker` não falhar).
    public var endRange: ClosedRange<Date> { startAt...max(startAt, maxSelectableTime) }

    // MARK: Edição de campos

    /// Duração em minutos (mamada, bomba, despertar): a UI edita a duração e o fim acompanha.
    public var durationMinutes: Int {
        get { max(0, Int(endAt.timeIntervalSince(startAt) / 60)) }
        set { endAt = startAt.addingTimeInterval(Double(max(0, newValue)) * 60) }
    }

    /// Muda o início preservando a duração (mamada, bomba, despertar); nos demais só o início muda.
    public func moveStart(to date: Date) {
        if showsDuration {
            let duration = endAt.timeIntervalSince(startAt)
            startAt = date
            endAt = date.addingTimeInterval(max(duration, 60))
        } else {
            startAt = date
        }
    }

    public func adjustVolume(by delta: Int) {
        volumeMl = min(EventValidator.maxVolumeMl, max(1, volumeMl + delta))
    }

    // MARK: Salvar

    /// Valida e grava local. Devolve `true` se salvou (a UI fecha o formulário).
    @discardableResult
    public func save() async -> Bool {
        guard tracking.canWrite else {
            banner = UserMessage("error.event_forbidden")
            return false
        }
        isSaving = true
        defer { isSaving = false }
        fieldErrors = [:]
        banner = nil

        // Mamada no peito exige o lado: nunca se assume um valor por conta do usuário.
        if kind == .breastfeeding, side == nil {
            fieldErrors["side"] = UserMessage("validation.required")
            return false
        }

        let saved: TrackedEvent?
        if let original {
            let changes = diff(from: original)
            guard !changes.isEmpty else {
                savedEvent = original
                return true
            }
            if !preflight(draftEvent: applied(changes, to: original)) { return false }
            saved = await tracking.edit(original, changes: changes)
        } else {
            let draft = makeDraft()
            let event = draft.build(id: UUID(), babyId: tracking.context.babyId, tz: tracking.context.timeZone,
                                    now: tracking.currentTime, author: nil)
            if !preflight(draftEvent: event) { return false }
            saved = await tracking.log(draft)
        }
        if let saved {
            savedEvent = saved
            return true
        }
        banner = tracking.banner ?? UserMessage.generic
        return false
    }

    private func preflight(draftEvent: TrackedEvent) -> Bool {
        let issues = EventValidator.validate(draftEvent, now: tracking.currentTime)
        guard !issues.isEmpty else { return true }
        var errors: [String: UserMessage] = [:]
        for issue in issues where errors[issue.field] == nil { errors[issue.field] = issue.message }
        fieldErrors = errors
        return false
    }

    private func applied(_ changes: EventChanges, to event: TrackedEvent) -> TrackedEvent {
        var copy = event
        _ = DefaultEventRepository.apply(changes, to: &copy)
        return copy
    }

    func makeDraft() -> EventDraft {
        let end: Date? = hasEnd ? endAt : nil
        switch kind {
        case .sleep:
            return .sleep(type: sleepType, start: startAt, end: end, source: .manual,
                          methodOrPlace: methodOrPlace, notes: notes)
        case .breastfeeding:
            // O lado vazio é barrado em `save()` antes de chegar aqui.
            return .breastfeeding(side: side ?? .both, start: startAt, end: endAt, notes: notes)
        case .bottle:
            return .bottle(start: startAt, end: end, volumeMl: volumeMl, milkType: milkType, notes: notes)
        case .pumping:
            return .pumping(start: startAt, end: endAt, volumeMl: includesVolume ? volumeMl : nil, side: side)
        case .otherFeeding:
            return .otherFeeding(type: .other, start: startAt, end: end, notes: notes)
        case .diaper:
            return .diaper(occurredAt: startAt, type: diaperType, notes: notes)
        case .wake(let sleepId):
            return .wake(sleepSessionId: sleepId, start: startAt, end: endAt, source: .manual)
        }
    }

    private func diff(from original: TrackedEvent) -> EventChanges {
        var changes = EventChanges()
        let start = startAt.roundedToSecond
        if start != original.startAt { changes.startAt = start }
        if showsEnd {
            let end: Date? = hasEnd ? endAt.roundedToSecond : nil
            if end != original.endAt { changes.endAt = end.map { Patch.set($0) } ?? .clear }
        }
        if showsSleepType, sleepType != (original.sleepType ?? .nap) { changes.sleepType = sleepType }
        if showsMethodOrPlace {
            let place = methodOrPlace.trimmingCharacters(in: .whitespacesAndNewlines)
            if place != (original.methodOrPlace ?? "") { changes.methodOrPlace = place.isEmpty ? .clear : .set(place) }
        }
        if showsNotes {
            let text = notes.trimmingCharacters(in: .whitespacesAndNewlines)
            if text != (original.notes ?? "") { changes.notes = text.isEmpty ? .clear : .set(text) }
        }
        if showsSide, side != original.side { changes.side = side.map { Patch.set($0) } ?? .clear }
        if showsVolume {
            let volume: Int? = (kind == .bottle || includesVolume) ? volumeMl : nil
            if volume != original.volumeMl { changes.volumeMl = volume.map { Patch.set($0) } ?? .clear }
        }
        if showsMilkType, milkType != original.milkType { changes.milkType = milkType.map { Patch.set($0) } ?? .clear }
        if showsDiaperType, diaperType != (original.diaperType ?? .wet) { changes.diaperType = diaperType }
        return changes
    }
}
