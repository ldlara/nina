import Foundation

/// Relógio injetado: nada no tracking chama `Date()` diretamente (testes, DST e fusos reprodutíveis).
public typealias NowProvider = @Sendable () -> Date

public enum NinaClock {
    public static let system: NowProvider = { Date() }
}

/// Cálculos de dia civil no fuso do bebê, seguros para horário de verão (dias de 23 ou 25 h).
public enum DayCalendar {
    public static func calendar(in timeZone: TimeZone) -> Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        return calendar
    }

    public static func day(of date: Date, in timeZone: TimeZone) -> CivilDate {
        CivilDate(date: date, timeZone: timeZone)
    }

    /// `[início do dia, início do dia seguinte)`. Parte do meio-dia (sempre existe) e usa `startOfDay`, que
    /// trata corretamente fusos em que a meia-noite não existe no dia da virada para o horário de verão.
    public static func interval(of day: CivilDate, in timeZone: TimeZone) -> Range<Date> {
        let calendar = calendar(in: timeZone)
        let start = calendar.startOfDay(for: day.date(in: timeZone))
        let next = adding(days: 1, to: day, in: timeZone)
        var end = calendar.startOfDay(for: next.date(in: timeZone))
        if end <= start { end = start.addingTimeInterval(86_400) }
        return start..<end
    }

    public static func adding(days: Int, to day: CivilDate, in timeZone: TimeZone) -> CivilDate {
        let calendar = calendar(in: timeZone)
        let noon = day.date(in: timeZone)
        let moved = calendar.date(byAdding: .day, value: days, to: noon) ?? noon.addingTimeInterval(Double(days) * 86_400)
        return CivilDate(date: moved, timeZone: timeZone)
    }

    /// Dia civil ao qual o evento pertence: no fuso **vigente quando foi registrado** (`tz` do evento, RB-014 /
    /// RF-010-A8), com o fuso do bebê como reserva. Sono que cruza a meia-noite conta no dia do **início**
    /// (D-13 ainda em aberto: assumido, ver README).
    public static func dayKey(for event: TrackedEvent, fallback: TimeZone) -> CivilDate {
        day(of: event.startAt, in: event.timeZone ?? fallback)
    }

    /// Hora local (0..23) do instante no fuso.
    public static func hour(of date: Date, in timeZone: TimeZone) -> Int {
        calendar(in: timeZone).component(.hour, from: date)
    }
}

/// Regra provisória para sugerir soneca × sono noturno (UX 4.2: "tipo inferido por horário/rotina, editável").
/// A rotina inicial (E5) está fora do escopo; as horas abaixo são premissa e ficam em um só lugar.
public struct SleepTypeRule: Equatable, Sendable {
    public var nightStartHour: Int
    public var nightEndHour: Int

    public init(nightStartHour: Int = 19, nightEndHour: Int = 6) {
        self.nightStartHour = nightStartHour
        self.nightEndHour = nightEndHour
    }

    public func infer(start: Date, in timeZone: TimeZone) -> SleepType {
        let hour = DayCalendar.hour(of: start, in: timeZone)
        return (hour >= nightStartHour || hour < nightEndHour) ? .night : .nap
    }
}

/// Duração decomposta para exibição (o formato localizado fica no app).
public struct DurationParts: Equatable, Sendable {
    public var hours: Int
    public var minutes: Int

    public init(seconds: TimeInterval) {
        let totalMinutes = Int(max(0, seconds) / 60)
        hours = totalMinutes / 60
        minutes = totalMinutes % 60
    }

    public var totalMinutes: Int { hours * 60 + minutes }
}

/// Atalhos de "Foi antes…" (UX 4.2).
public enum RetroactiveShortcut {
    public static let minutes: [Int] = [5, 10, 15, 30]
}
