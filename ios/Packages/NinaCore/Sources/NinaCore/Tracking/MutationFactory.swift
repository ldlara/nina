import Foundation

/// Converte eventos locais em mutações do `POST /sync/push` (openapi v1.0.1). Os schemas de criação têm
/// `additionalProperties: false`: só as chaves permitidas para o tipo são enviadas.
public enum MutationFactory {
    public static func create(for event: TrackedEvent, mutationId: UUID = UUID(), deviceId: UUID,
                              now: Date) -> QueuedMutation {
        QueuedMutation(mutationId: mutationId, op: .create, entityType: event.kind.syncEntityType,
                       entityId: event.id, babyId: event.babyId, baseVersion: 0, clientCreatedAt: now.roundedToSecond,
                       deviceId: deviceId, data: createData(for: event),
                       dependsOn: event.kind == .wake ? event.sleepSessionId : nil)
    }

    public static func update(for event: TrackedEvent, changes: EventChanges, baseVersion: Int,
                              mutationId: UUID = UUID(), deviceId: UUID, now: Date) -> QueuedMutation {
        QueuedMutation(mutationId: mutationId, op: .update, entityType: event.kind.syncEntityType,
                       entityId: event.id, babyId: event.babyId, baseVersion: baseVersion,
                       clientCreatedAt: now.roundedToSecond, deviceId: deviceId,
                       data: patchData(for: event, changes: changes),
                       dependsOn: event.kind == .wake ? event.sleepSessionId : nil)
    }

    public static func delete(for event: TrackedEvent, baseVersion: Int, mutationId: UUID = UUID(),
                              deviceId: UUID, now: Date, notBefore: Date? = nil) -> QueuedMutation {
        QueuedMutation(mutationId: mutationId, op: .delete, entityType: event.kind.syncEntityType,
                       entityId: event.id, babyId: event.babyId, baseVersion: baseVersion,
                       clientCreatedAt: now.roundedToSecond, deviceId: deviceId, data: nil, nextAttemptAt: notBefore)
    }

    // MARK: data de CREATE

    static func createData(for event: TrackedEvent) -> JSONValue {
        var data: [String: JSONValue] = [:]
        switch event.kind {
        case .sleep:
            data["sleep_type"] = .string((event.sleepType ?? .nap).rawValue)
            data["start_at"] = .instant(event.startAt)
            data["end_at"] = .optionalInstant(event.endAt)
            data["tz"] = .string(event.tz)
            data["source"] = .string((event.sleepSource ?? .manual).rawValue)
            if let place = event.methodOrPlace { data["method_or_place"] = .string(place) }
            if let notes = event.notes { data["notes"] = .string(notes) }

        case .feeding:
            let type = event.feedingType ?? .other
            data["feeding_type"] = .string(type.rawValue)
            data["start_at"] = .instant(event.startAt)
            data["tz"] = .string(event.tz)
            if let notes = event.notes { data["notes"] = .string(notes) }
            switch type {
            case .breastfeeding:
                data["side"] = .string((event.side ?? .both).rawValue)
                data["end_at"] = .optionalInstant(event.endAt)
            case .bottle:
                data["end_at"] = .optionalInstant(event.endAt)
                data["volume_ml"] = event.volumeMl.map { JSONValue.int($0) } ?? JSONValue.null
                // `milk_type` nulo = não informado (só mamadeira aceita o campo).
                data["milk_type"] = event.milkType.map { JSONValue.string($0.rawValue) } ?? JSONValue.null
            default:
                data["end_at"] = .optionalInstant(event.endAt)
            }

        case .pumping:
            // `PumpingData` não aceita `notes`.
            data["start_at"] = .instant(event.startAt)
            data["end_at"] = .optionalInstant(event.endAt)
            data["tz"] = .string(event.tz)
            data["volume_ml"] = event.volumeMl.map { JSONValue.int($0) } ?? JSONValue.null
            data["side"] = event.side.map { JSONValue.string($0.rawValue) } ?? JSONValue.null

        case .diaper:
            data["occurred_at"] = .instant(event.startAt)
            data["diaper_type"] = .string((event.diaperType ?? .unspecified).rawValue)
            data["tz"] = .string(event.tz)
            if let notes = event.notes { data["notes"] = .string(notes) }

        case .wake:
            // `WakeEventData` não tem `tz`.
            if let sleepId = event.sleepSessionId { data["sleep_session_id"] = .string(sleepId.uuidString.lowercased()) }
            data["started_at"] = .instant(event.startAt)
            data["ended_at"] = .optionalInstant(event.endAt)
            data["source"] = .string((event.wakeSource ?? .manual).rawValue)
        }
        return .object(data)
    }

    // MARK: data de UPDATE (EventPatch)

    static func patchData(for event: TrackedEvent, changes: EventChanges) -> JSONValue {
        var data: [String: JSONValue] = [:]
        let isWake = event.kind == .wake
        let isDiaper = event.kind == .diaper

        if let sleepType = changes.sleepType { data["sleep_type"] = .string(sleepType.rawValue) }
        if let start = changes.startAt {
            let key = isWake ? "started_at" : (isDiaper ? "occurred_at" : "start_at")
            data[key] = .instant(start)
        }
        switch changes.endAt {
        case .unchanged: break
        case .set(let end): data[isWake ? "ended_at" : "end_at"] = .instant(end)
        case .clear: data[isWake ? "ended_at" : "end_at"] = .null
        }
        if let tz = changes.tz { data["tz"] = .string(tz) }
        put(&data, "method_or_place", changes.methodOrPlace) { .string($0) }
        put(&data, "notes", changes.notes) { .string($0) }
        put(&data, "side", changes.side) { .string($0.rawValue) }
        put(&data, "volume_ml", changes.volumeMl) { .int($0) }
        // `milk_type` só em mamadeira: ignora tentativa em outros tipos.
        if event.feedingType == .bottle { put(&data, "milk_type", changes.milkType) { .string($0.rawValue) } }
        if let diaper = changes.diaperType { data["diaper_type"] = .string(diaper.rawValue) }
        if let source = changes.wakeSource { data["source"] = .string(source.rawValue) }
        return .object(data)
    }

    private static func put<Value>(_ data: inout [String: JSONValue], _ key: String, _ patch: Patch<Value>,
                                   _ convert: (Value) -> JSONValue) {
        switch patch {
        case .unchanged: break
        case .set(let value): data[key] = convert(value)
        case .clear: data[key] = .null
        }
    }
}
