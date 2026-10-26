import Foundation

/// Evento de tracking no banco local (sono, mamada/mamadeira, bomba, fralda ou despertar).
///
/// Um único tipo "achatado" espelha os schemas do contrato (`SleepSession`, `FeedingSession`, `PumpingSession`,
/// `DiaperEvent`, `WakeEvent`). Campos que não se aplicam ao `kind` ficam `nil` (ADR-0009: `nil` = não se aplica;
/// `0` nunca significa "não informado"). Persistido como JSON: campos novos devem ser sempre opcionais.
///
/// Mapa de nomes: `startAt` = `start_at` | `occurred_at` (fralda) | `started_at` (despertar);
/// `endAt` = `end_at` | `ended_at` (despertar).
public struct TrackedEvent: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var babyId: UUID
    public var kind: EventKind
    /// Última versão canônica conhecida do servidor. `0` = o servidor ainda não confirmou a criação.
    public var version: Int
    /// IANA vigente quando registrado (RB-014).
    public var tz: String
    public var startAt: Date
    public var endAt: Date?

    // Sono
    public var sleepType: SleepType?
    public var sleepSource: SleepSource?
    public var methodOrPlace: String?
    /// Derivado dos despertares (somente leitura, vem do servidor). `nil` = não se aplica / insuficiente.
    public var nightAwakenings: Int?

    // Alimentação / bomba
    public var feedingType: FeedingType?
    public var side: BreastSide?
    public var volumeMl: Int?
    /// Só quando `feedingType == .bottle`.
    public var milkType: MilkType?

    // Fralda
    public var diaperType: DiaperType?

    // Despertar
    public var sleepSessionId: UUID?
    public var wakeSource: WakeSource?

    public var notes: String?

    public var createdAt: Date
    public var updatedAt: Date
    public var createdBy: UserRef?
    public var lastModifiedBy: UserRef?

    // Estado local
    public var syncStatus: EventSyncStatus
    /// Tombstone local: o evento foi excluído neste aparelho e some das telas; a linha só é removida quando o
    /// servidor confirma a exclusão (ou quando a criação nunca saiu do aparelho).
    public var deletedAt: Date?

    public init(id: UUID, babyId: UUID, kind: EventKind, version: Int = 0, tz: String, startAt: Date,
                endAt: Date? = nil, sleepType: SleepType? = nil, sleepSource: SleepSource? = nil,
                methodOrPlace: String? = nil, nightAwakenings: Int? = nil, feedingType: FeedingType? = nil,
                side: BreastSide? = nil, volumeMl: Int? = nil, milkType: MilkType? = nil,
                diaperType: DiaperType? = nil, sleepSessionId: UUID? = nil, wakeSource: WakeSource? = nil,
                notes: String? = nil, createdAt: Date, updatedAt: Date, createdBy: UserRef? = nil,
                lastModifiedBy: UserRef? = nil, syncStatus: EventSyncStatus = .pending, deletedAt: Date? = nil) {
        self.id = id
        self.babyId = babyId
        self.kind = kind
        self.version = version
        self.tz = tz
        self.startAt = startAt
        self.endAt = endAt
        self.sleepType = sleepType
        self.sleepSource = sleepSource
        self.methodOrPlace = methodOrPlace
        self.nightAwakenings = nightAwakenings
        self.feedingType = feedingType
        self.side = side
        self.volumeMl = volumeMl
        self.milkType = milkType
        self.diaperType = diaperType
        self.sleepSessionId = sleepSessionId
        self.wakeSource = wakeSource
        self.notes = notes
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.createdBy = createdBy
        self.lastModifiedBy = lastModifiedBy
        self.syncStatus = syncStatus
        self.deletedAt = deletedAt
    }

    public var isDeleted: Bool { deletedAt != nil }

    /// Sono em andamento (timer): sessão de sono sem fim e não excluída.
    public var isOpenSleep: Bool { kind == .sleep && endAt == nil && deletedAt == nil }

    /// Duração derivada (nunca persistida). `nil` se em aberto ou sem fim.
    public var duration: TimeInterval? {
        guard let endAt else { return nil }
        return max(0, endAt.timeIntervalSince(startAt))
    }

    public var timeZone: TimeZone? { TimeZone(identifier: tz) }

    /// Instante usado para ordenar a timeline (decrescente), com desempate por `id`.
    public var sortDate: Date { startAt }
}

/// Dados para criar um evento. Cada caso carrega só os campos que se aplicam ao tipo (INV do contrato:
/// `milk_type` só em mamadeira, `side` só em peito/bomba, `volume_ml` só em mamadeira/bomba).
public enum EventDraft: Equatable, Sendable {
    case sleep(type: SleepType, start: Date, end: Date?, source: SleepSource, methodOrPlace: String?, notes: String?)
    /// Mamada no peito: `end` é obrigatório (ADR-0010, decisão 5).
    case breastfeeding(side: BreastSide, start: Date, end: Date, notes: String?)
    case bottle(start: Date, end: Date?, volumeMl: Int, milkType: MilkType?, notes: String?)
    /// Sólido ou outro (`SOLID`/`OTHER`): sem lado, volume nem tipo de leite.
    case otherFeeding(type: FeedingType, start: Date, end: Date?, notes: String?)
    case pumping(start: Date, end: Date, volumeMl: Int?, side: BreastSide?)
    case diaper(occurredAt: Date, type: DiaperType, notes: String?)
    case wake(sleepSessionId: UUID, start: Date, end: Date, source: WakeSource)

    public var kind: EventKind {
        switch self {
        case .sleep: return .sleep
        case .breastfeeding, .bottle, .otherFeeding: return .feeding
        case .pumping: return .pumping
        case .diaper: return .diaper
        case .wake: return .wake
        }
    }

    /// Cria o evento local (versão 0, pendente). Horários são truncados para segundos inteiros, o que o
    /// contrato transmite; assim o que está no banco local é exatamente o que o servidor devolverá.
    public func build(id: UUID, babyId: UUID, tz: String, now: Date, author: UserRef?) -> TrackedEvent {
        let stamp = now.roundedToSecond
        var event = TrackedEvent(id: id, babyId: babyId, kind: kind, version: 0, tz: tz, startAt: stamp,
                                 createdAt: stamp, updatedAt: stamp, createdBy: author, lastModifiedBy: author,
                                 syncStatus: .pending)
        switch self {
        case let .sleep(type, start, end, source, method, notes):
            event.sleepType = type
            event.sleepSource = source
            event.startAt = start.roundedToSecond
            event.endAt = end?.roundedToSecond
            event.methodOrPlace = method.nilIfBlank
            event.notes = notes.nilIfBlank
        case let .breastfeeding(side, start, end, notes):
            event.feedingType = .breastfeeding
            event.side = side
            event.startAt = start.roundedToSecond
            event.endAt = end.roundedToSecond
            event.notes = notes.nilIfBlank
        case let .bottle(start, end, volume, milk, notes):
            event.feedingType = .bottle
            event.startAt = start.roundedToSecond
            event.endAt = end?.roundedToSecond
            event.volumeMl = volume
            event.milkType = milk
            event.notes = notes.nilIfBlank
        case let .otherFeeding(type, start, end, notes):
            event.feedingType = type
            event.startAt = start.roundedToSecond
            event.endAt = end?.roundedToSecond
            event.notes = notes.nilIfBlank
        case let .pumping(start, end, volume, side):
            event.startAt = start.roundedToSecond
            event.endAt = end.roundedToSecond
            event.volumeMl = volume
            event.side = side
        case let .diaper(occurredAt, type, notes):
            event.startAt = occurredAt.roundedToSecond
            event.diaperType = type
            event.notes = notes.nilIfBlank
        case let .wake(sleepId, start, end, source):
            event.sleepSessionId = sleepId
            event.startAt = start.roundedToSecond
            event.endAt = end.roundedToSecond
            event.wakeSource = source
        }
        return event
    }
}

/// Alterações parciais de um evento (espelha `EventPatch`: só os campos presentes entram no `UPDATE`;
/// `Patch.clear` envia `null` nos campos anuláveis). `feeding_type` não muda: exclua e recrie.
public struct EventChanges: Equatable, Sendable {
    public var sleepType: SleepType?
    public var startAt: Date?
    public var endAt: Patch<Date> = .unchanged
    public var tz: String?
    public var methodOrPlace: Patch<String> = .unchanged
    public var notes: Patch<String> = .unchanged
    public var side: Patch<BreastSide> = .unchanged
    public var volumeMl: Patch<Int> = .unchanged
    public var milkType: Patch<MilkType> = .unchanged
    public var diaperType: DiaperType?
    public var wakeSource: WakeSource?

    public init() {}

    public var isEmpty: Bool {
        sleepType == nil && startAt == nil && endAt == .unchanged && tz == nil && methodOrPlace == .unchanged
            && notes == .unchanged && side == .unchanged && volumeMl == .unchanged && milkType == .unchanged
            && diaperType == nil && wakeSource == nil
    }
}

extension Date {
    /// Trunca para segundos inteiros (o contrato transmite RFC 3339 em segundos).
    public var roundedToSecond: Date {
        Date(timeIntervalSince1970: timeIntervalSince1970.rounded(.down))
    }
}

extension Optional where Wrapped == String {
    /// Texto livre vazio ou só com espaços vira `nil` (nada de "" no contrato).
    var nilIfBlank: String? {
        guard let trimmed = self?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else { return nil }
        return trimmed
    }
}

extension String {
    var nilIfBlank: String? { Optional(self).nilIfBlank }
}
