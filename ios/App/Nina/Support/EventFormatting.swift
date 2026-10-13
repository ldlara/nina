import SwiftUI
import NinaCore

/// Textos, ícones e cores dos eventos. Todo texto vem do catálogo (`Localizable.strings`, pt-BR/en/es); horários
/// são formatados no **fuso do evento** (RB-014) com o locale do aparelho. Cor nunca é o único sinal: cada tipo
/// tem ícone e texto próprios.
enum EventFormatting {
    private static func text(_ key: String) -> String { NSLocalizedString(key, comment: "") }

    // MARK: Horários e durações

    static func time(_ date: Date, timeZone: TimeZone) -> String {
        let formatter = DateFormatter()
        formatter.locale = .autoupdatingCurrent
        formatter.timeZone = timeZone
        formatter.dateStyle = .none
        formatter.timeStyle = .short
        return formatter.string(from: date)
    }

    static func timeRange(start: Date, end: Date?, timeZone: TimeZone) -> String {
        guard let end else { return time(start, timeZone: timeZone) }
        return time(start, timeZone: timeZone) + " – " + time(end, timeZone: timeZone)
    }

    /// "1 h 20 min" / "45 min" (abreviado e localizado pelo sistema).
    static func duration(_ seconds: TimeInterval) -> String {
        guard seconds >= 60 else { return text("duration.less_than_minute") }
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = [.hour, .minute]
        formatter.unitsStyle = .abbreviated
        formatter.zeroFormattingBehavior = .dropAll
        return formatter.string(from: seconds) ?? ""
    }

    /// Versão falada ("1 hora e 20 minutos").
    static func spokenDuration(_ seconds: TimeInterval) -> String {
        guard seconds >= 60 else { return text("duration.less_than_minute") }
        let formatter = DateComponentsFormatter()
        formatter.allowedUnits = [.hour, .minute]
        formatter.unitsStyle = .full
        formatter.zeroFormattingBehavior = .dropAll
        return formatter.string(from: seconds) ?? ""
    }

    /// Cronômetro "0:42:17" (dígitos tabulares na UI).
    static func clock(_ seconds: TimeInterval) -> String {
        let total = Int(max(0, seconds))
        return String(format: "%d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
    }

    /// Título do dia na timeline: "Hoje", "Ontem" ou "qui., 8 de out.".
    static func dayTitle(_ day: CivilDate, today: CivilDate, timeZone: TimeZone) -> String {
        if day == today { return text("day.today") }
        if DayCalendar.adding(days: -1, to: today, in: timeZone) == day { return text("day.yesterday") }
        let formatter = DateFormatter()
        formatter.locale = .autoupdatingCurrent
        formatter.timeZone = timeZone
        formatter.setLocalizedDateFormatFromTemplate("EEEdMMM")
        return formatter.string(from: day.date(in: timeZone))
    }

    // MARK: Enums

    static func name(_ side: BreastSide) -> String {
        switch side {
        case .left: return text("side.left")
        case .right: return text("side.right")
        case .both: return text("side.both")
        case .unknown: return text("enum.unknown")
        }
    }

    static func name(_ type: DiaperType) -> String {
        switch type {
        case .wet: return text("diaper.wet")
        case .dirty: return text("diaper.dirty")
        case .mixed: return text("diaper.mixed")
        case .dry: return text("diaper.dry")
        case .unspecified: return text("diaper.unspecified")
        case .unknown: return text("enum.unknown")
        }
    }

    static func name(_ type: MilkType) -> String {
        switch type {
        case .breastMilk: return text("milk.breast_milk")
        case .formula: return text("milk.formula")
        case .mixed: return text("milk.mixed")
        case .other: return text("milk.other")
        case .unspecified: return text("milk.unspecified")
        case .unknown: return text("enum.unknown")
        }
    }

    static func name(_ type: SleepType) -> String {
        switch type {
        case .nap: return text("event.sleep.nap")
        case .night: return text("event.sleep.night")
        case .unknown: return text("event.sleep.generic")
        }
    }

    static func volume(_ ml: Int) -> String {
        String(format: text("event.volume %lld"), ml)
    }

    // MARK: Eventos

    static func title(for event: TrackedEvent) -> String {
        switch event.kind {
        case .sleep:
            if event.endAt == nil { return text("event.sleep.open") }
            return name(event.sleepType ?? .nap)
        case .feeding:
            switch event.feedingType {
            case .some(.breastfeeding):
                if let side = event.side { return text("event.feeding.breast") + " · " + name(side) }
                return text("event.feeding.breast")
            case .some(.bottle):
                if let volume = event.volumeMl { return text("event.feeding.bottle") + " · " + Self.volume(volume) }
                return text("event.feeding.bottle")
            default:
                return text("event.feeding.other")
            }
        case .pumping:
            if let volume = event.volumeMl { return text("event.pumping") + " · " + Self.volume(volume) }
            return text("event.pumping")
        case .diaper:
            return text("event.diaper") + " · " + name(event.diaperType ?? .unspecified)
        case .wake:
            return text("event.wake")
        }
    }

    /// Linha secundária: horário/intervalo e duração.
    static func subtitle(for event: TrackedEvent, fallback: TimeZone) -> String {
        let tz = event.timeZone ?? fallback
        switch event.kind {
        case .diaper:
            return time(event.startAt, timeZone: tz)
        default:
            guard let duration = event.duration else {
                return String(format: text("event.since %@"), time(event.startAt, timeZone: tz))
            }
            return timeRange(start: event.startAt, end: event.endAt, timeZone: tz) + " · " + Self.duration(duration)
        }
    }

    static func symbol(for event: TrackedEvent) -> String {
        switch event.kind {
        case .sleep: return "moon.zzz.fill"
        case .feeding: return event.feedingType == .bottle ? "drop.fill" : "heart.fill"
        case .pumping: return "arrow.triangle.2.circlepath"
        case .diaper: return "diamond.fill"
        case .wake: return "sun.max.fill"
        }
    }

    static func token(for kind: EventKind) -> NinaColorToken {
        switch kind {
        case .sleep, .wake: return .eventSleep
        case .feeding: return .eventFeeding
        case .diaper: return .eventDiaper
        case .pumping: return .eventPumping
        }
    }

    static func syncLabel(for event: TrackedEvent) -> String? {
        switch event.syncStatus {
        case .synced: return nil
        case .pending: return text("event.pending")
        case .rejected: return text("event.rejected")
        }
    }

    /// Texto único para o VoiceOver: tipo, horário, duração, estado de envio e autoria.
    static func accessibilityLabel(for event: TrackedEvent, fallback: TimeZone, showAuthor: Bool) -> String {
        var parts = [title(for: event), subtitle(for: event, fallback: fallback)]
        if let duration = event.duration, event.kind != .diaper { parts.append(spokenDuration(duration)) }
        if let sync = syncLabel(for: event) { parts.append(sync) }
        if showAuthor, let author = event.lastModifiedBy?.displayName, !author.isEmpty {
            parts.append(String(format: text("event.edited_by %@"), author))
        }
        return parts.joined(separator: ", ")
    }
}
