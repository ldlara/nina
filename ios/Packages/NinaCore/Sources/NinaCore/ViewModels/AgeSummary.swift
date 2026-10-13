import Foundation

/// Idade pronta para exibir. Vem de `Baby.age` / `Baby.age_calculation` (fonte canônica no servidor,
/// ADR-0009): o cliente **não recalcula** a política de idade corrigida nem a persiste.
public struct AgeSummary: Equatable, Sendable {
    public struct Parts: Equatable, Sendable {
        public var months: Int
        public var weeks: Int
        public var days: Int
    }

    public var chronological: Parts
    /// `nil` = a correção não se aplica; a UI não mostra idade corrigida (RF-005-A2/A6).
    public var corrected: Parts?

    public init?(baby: Baby) {
        if let age = baby.age {
            chronological = Parts(months: age.chronological.months, weeks: age.chronological.weeks,
                                  days: age.chronological.days)
            corrected = age.corrected.map { Parts(months: $0.months, weeks: $0.weeks, days: $0.days) }
            return
        }
        guard let calc = baby.ageCalculation else { return nil }
        chronological = Self.approximate(days: calc.chronologicalDays)
        if calc.correctionApplied, let days = calc.correctedDays {
            corrected = Self.approximate(days: days)
        } else {
            corrected = nil
        }
    }

    /// Só para exibição quando `age` não veio: converte dias em semanas/meses aproximados (30,4375 d/mês).
    static func approximate(days: Int) -> Parts {
        let safe = max(0, days)
        return Parts(months: Int(Double(safe) / 30.4375), weeks: safe / 7, days: safe)
    }
}
