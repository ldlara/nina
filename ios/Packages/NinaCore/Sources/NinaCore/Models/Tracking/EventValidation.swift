import Foundation

/// Problema de validação de um campo. `code` vira a chave `validation.<code>` (mensagem localizada no app).
public struct FieldIssue: Equatable, Hashable, Sendable {
    public var field: String
    public var code: String

    public init(field: String, code: String) {
        self.field = field
        self.code = code
    }

    public var message: UserMessage { UserMessage("validation." + code) }
}

/// Regras de validação local de eventos (RF-008-A2, RF-015..018, contrato v1.0.1). O servidor continua sendo a
/// autoridade (limites efetivos vêm de `GET /reference-data`, ainda não consumido: ver README).
public enum EventValidator {
    public static let maxNotesLength = 500
    public static let maxMethodOrPlaceLength = 80
    /// Teto absoluto do contrato para volumes (`maximum: 5000`).
    public static let maxVolumeMl = 5000
    /// Tolerância para horários "no futuro" (relógio do aparelho x digitação). Registros para depois disso são recusados.
    public static let futureToleranceSeconds: TimeInterval = 300

    public static func validate(_ event: TrackedEvent, now: Date) -> [FieldIssue] {
        var issues: [FieldIssue] = []
        let limit = now.addingTimeInterval(futureToleranceSeconds)

        if TimeZone(identifier: event.tz) == nil { issues.append(.init(field: "tz", code: "timezone_invalid")) }
        if event.startAt > limit { issues.append(.init(field: "startAt", code: "future_time")) }
        if let end = event.endAt, end > limit { issues.append(.init(field: "endAt", code: "future_time")) }
        if let notes = event.notes, notes.count > maxNotesLength { issues.append(.init(field: "notes", code: "notes_too_long")) }

        switch event.kind {
        case .sleep:
            if event.sleepType == nil { issues.append(.init(field: "sleepType", code: "required")) }
            checkOrder(event, strict: true, into: &issues)
            if let place = event.methodOrPlace, place.count > maxMethodOrPlaceLength {
                issues.append(.init(field: "methodOrPlace", code: "method_too_long"))
            }
            if event.side != nil || event.volumeMl != nil || event.milkType != nil || event.feedingType != nil {
                issues.append(.init(field: "kind", code: "not_applicable"))
            }

        case .feeding:
            guard let type = event.feedingType else {
                issues.append(.init(field: "feedingType", code: "required"))
                break
            }
            if type == .breastfeeding {
                if event.side == nil { issues.append(.init(field: "side", code: "required")) }
                if event.endAt == nil { issues.append(.init(field: "endAt", code: "end_required")) }
                if event.volumeMl != nil || event.milkType != nil {
                    issues.append(.init(field: "kind", code: "not_applicable"))
                }
                checkOrder(event, strict: true, into: &issues)
            } else if type == .bottle {
                checkVolume(event.volumeMl, required: true, into: &issues)
                // Mamadeira pode ter fim opcional; fim igual ao início é aceito.
                checkOrder(event, strict: false, into: &issues)
                if event.side != nil { issues.append(.init(field: "side", code: "not_applicable")) }
            } else {
                // SOLID / OTHER / desconhecido: milk_type, side e volume não se aplicam.
                if event.side != nil || event.volumeMl != nil || event.milkType != nil {
                    issues.append(.init(field: "kind", code: "not_applicable"))
                }
                checkOrder(event, strict: false, into: &issues)
            }

        case .pumping:
            if event.endAt == nil { issues.append(.init(field: "endAt", code: "end_required")) }
            checkVolume(event.volumeMl, required: false, into: &issues)
            checkOrder(event, strict: true, into: &issues)
            if event.milkType != nil { issues.append(.init(field: "milkType", code: "not_applicable")) }

        case .diaper:
            if event.diaperType == nil { issues.append(.init(field: "diaperType", code: "required")) }
            if event.endAt != nil { issues.append(.init(field: "endAt", code: "not_applicable")) }

        case .wake:
            if event.sleepSessionId == nil { issues.append(.init(field: "sleepSessionId", code: "required")) }
            if event.endAt == nil { issues.append(.init(field: "endAt", code: "end_required")) }
            checkOrder(event, strict: true, into: &issues)
        }
        return issues
    }

    private static func checkOrder(_ event: TrackedEvent, strict: Bool, into issues: inout [FieldIssue]) {
        guard let end = event.endAt else { return }
        let invalid = strict ? (end <= event.startAt) : (end < event.startAt)
        if invalid {
            issues.append(.init(field: "endAt", code: "end_before_start"))
        }
    }

    private static func checkVolume(_ volume: Int?, required: Bool, into issues: inout [FieldIssue]) {
        guard let volume else {
            if required { issues.append(.init(field: "volumeMl", code: "volume_required")) }
            return
        }
        if volume < 1 || volume > maxVolumeMl { issues.append(.init(field: "volumeMl", code: "volume_range")) }
    }

    /// Despertar precisa caber dentro da sessão de sono (o servidor também valida o vínculo).
    public static func validateWake(_ wake: TrackedEvent, within sleep: TrackedEvent, now: Date) -> [FieldIssue] {
        var issues: [FieldIssue] = []
        let sleepEnd = sleep.endAt ?? now.addingTimeInterval(futureToleranceSeconds)
        if wake.startAt < sleep.startAt || (wake.endAt ?? wake.startAt) > sleepEnd {
            issues.append(.init(field: "startAt", code: "wake_outside_sleep"))
        }
        return issues
    }
}
