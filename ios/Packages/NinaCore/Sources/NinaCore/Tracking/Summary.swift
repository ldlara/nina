import Foundation

/// Filtros por chips da timeline (UX 4.4): Tudo, Sono, Comida, Fralda, Pumping.
public enum TimelineFilter: String, CaseIterable, Sendable {
    case all, sleep, food, diaper, pumping

    public func includes(_ event: TrackedEvent) -> Bool {
        switch self {
        case .all: return true
        case .sleep: return event.kind == .sleep
        case .food: return event.kind == .feeding
        case .diaper: return event.kind == .diaper
        case .pumping: return event.kind == .pumping
        }
    }
}

/// Totais compactos do dia (RF-010). Tudo é derivado dos eventos, nunca persistido (RF-010-A7).
public struct DaySummary: Equatable, Sendable {
    public var day: CivilDate
    /// Soma das sessões de sono **fechadas** (RF-010-A6: em andamento não entra).
    public var sleepSeconds: TimeInterval
    public var napCount: Int
    public var hasOpenSleep: Bool
    public var feedingCount: Int
    public var bottleVolumeMl: Int
    public var diaperCount: Int
    public var pumpingCount: Int

    public var isEmpty: Bool {
        sleepSeconds == 0 && napCount == 0 && !hasOpenSleep && feedingCount == 0 && diaperCount == 0 && pumpingCount == 0
    }

    /// `events` deve conter só eventos do dia (use `DayCalendar.dayKey`). Despertares e tombstones não contam.
    public static func make(day: CivilDate, events: [TrackedEvent]) -> DaySummary {
        var summary = DaySummary(day: day, sleepSeconds: 0, napCount: 0, hasOpenSleep: false, feedingCount: 0,
                                 bottleVolumeMl: 0, diaperCount: 0, pumpingCount: 0)
        for event in events where !event.isDeleted {
            switch event.kind {
            case .sleep:
                if let duration = event.duration {
                    summary.sleepSeconds += duration
                    if event.sleepType == .nap { summary.napCount += 1 }
                } else {
                    summary.hasOpenSleep = true
                }
            case .feeding:
                summary.feedingCount += 1
                if event.feedingType == .bottle, let volume = event.volumeMl { summary.bottleVolumeMl += volume }
            case .diaper:
                summary.diaperCount += 1
            case .pumping:
                summary.pumpingCount += 1
            case .wake:
                break
            }
        }
        return summary
    }
}

/// Valores sugeridos pelos últimos registros (UX 2: "agora, último lado, último volume").
public struct LastUsedValues: Equatable, Sendable {
    public var bottleVolumeMl: Int?
    public var milkType: MilkType?
    public var breastSide: BreastSide?
    public var diaperType: DiaperType?

    /// `events` em ordem decrescente de `startAt`.
    public init(events: [TrackedEvent]) {
        for event in events where !event.isDeleted {
            if event.kind == .feeding, event.feedingType == .bottle {
                if bottleVolumeMl == nil { bottleVolumeMl = event.volumeMl }
                if milkType == nil { milkType = event.milkType }
            }
            if event.kind == .feeding, event.feedingType == .breastfeeding, breastSide == nil { breastSide = event.side }
            if event.kind == .diaper, diaperType == nil { diaperType = event.diaperType }
        }
    }
}

/// Ações da folha de registro rápido (UX 4.3), ordenadas por uso recente.
public enum QuickAction: String, CaseIterable, Sendable {
    case sleep, breastfeeding, bottle, diaper, pumping

    static func action(for event: TrackedEvent) -> QuickAction? {
        switch event.kind {
        case .sleep: return .sleep
        case .feeding: return event.feedingType == .bottle ? .bottle : .breastfeeding
        case .diaper: return .diaper
        case .pumping: return .pumping
        case .wake: return nil
        }
    }

    /// `events` em ordem decrescente: quem foi usado mais recentemente vem primeiro; os nunca usados
    /// ficam depois, na ordem padrão.
    public static func order(from events: [TrackedEvent]) -> [QuickAction] {
        var result: [QuickAction] = []
        for event in events where !event.isDeleted {
            if let action = action(for: event), !result.contains(action) { result.append(action) }
        }
        for action in allCases where !result.contains(action) { result.append(action) }
        return result
    }
}
