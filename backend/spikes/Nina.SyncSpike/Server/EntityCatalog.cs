using System.Globalization;
using System.Text.Json;
using NpgsqlTypes;

namespace Nina.SyncSpike.Server;

public enum FieldKind { Text, Timestamp, Number, Uuid }

/// <param name="CreateOnly">Imutável depois da criação (ex.: feeding_type, sleep_session_id).</param>
/// <param name="RequiredOnCreate">Obrigatório no CREATE (contrato).</param>
public sealed record FieldSpec(string Name, FieldKind Kind, bool Nullable = true, bool CreateOnly = false, bool RequiredOnCreate = false);

/// <param name="Type">Nome no contrato de push (SyncEntityType).</param>
/// <param name="Table">Tabela física em schema nina.</param>
/// <param name="LogType">entity_type gravado em change_log.</param>
public sealed record EntitySpec(string Type, string Table, string LogType, IReadOnlyList<FieldSpec> Fields)
{
    public FieldSpec? Find(string name) => Fields.FirstOrDefault(f => f.Name == name);
    public string Qualified => "nina." + Table;
}

public static class EntityCatalog
{
    private static FieldSpec T(string n, bool nullable = true, bool create = false, bool req = false) => new(n, FieldKind.Text, nullable, create, req);
    private static FieldSpec Ts(string n, bool nullable = true, bool req = false) => new(n, FieldKind.Timestamp, nullable, false, req);

    public static readonly IReadOnlyDictionary<string, EntitySpec> Push = new Dictionary<string, EntitySpec>
    {
        ["SLEEP_SESSION"] = new("SLEEP_SESSION", "sleep_session", "SLEEP_SESSION",
        [
            T("sleep_type", false, req: true), Ts("start_at", false, true), Ts("end_at"), T("tz", false, req: true),
            T("method_or_place"), T("notes"), T("source", false, create: true, req: true),
        ]),
        ["FEEDING_SESSION"] = new("FEEDING_SESSION", "feeding_session", "FEEDING_SESSION",
        [
            T("feeding_type", false, create: true, req: true), Ts("start_at", false, true), Ts("end_at"), T("tz", false, req: true),
            T("side"), new("volume_ml", FieldKind.Number), T("milk_type"), T("notes"),
        ]),
        ["PUMPING_SESSION"] = new("PUMPING_SESSION", "pumping_session", "PUMPING_SESSION",
        [
            Ts("start_at", false, true), Ts("end_at", true, true), T("tz", false, req: true),
            new("volume_ml", FieldKind.Number), T("side"), T("notes"),
        ]),
        ["DIAPER_EVENT"] = new("DIAPER_EVENT", "diaper_event", "DIAPER_EVENT",
        [
            Ts("occurred_at", false, true), T("tz", false, req: true), T("diaper_type", false, req: true), T("notes"),
        ]),
        ["WAKE_EVENT"] = new("WAKE_EVENT", "wake_event", "WAKE_EVENT",
        [
            new("sleep_session_id", FieldKind.Uuid, false, true, true), Ts("started_at", false, true), Ts("ended_at", true, true),
            T("source", false),
        ]),
    };

    /// <summary>Tabelas do feed de pull, na ordem do snapshot (rank). Nome de contrato -> tabela.</summary>
    public static readonly IReadOnlyList<(int Rank, string LogType, string ContractType, string Table, string BabyCol)> Feed =
    [
        (0, "BABY", "BABY", "baby", "id"),
        (1, "SLEEP_SESSION", "SLEEP_SESSION", "sleep_session", "baby_id"),
        (2, "FEEDING_SESSION", "FEEDING_SESSION", "feeding_session", "baby_id"),
        (3, "PUMPING_SESSION", "PUMPING_SESSION", "pumping_session", "baby_id"),
        (4, "DIAPER_EVENT", "DIAPER_EVENT", "diaper_event", "baby_id"),
        (5, "WAKE_EVENT", "WAKE_EVENT", "wake_event", "baby_id"),
        (6, "SLEEP_SCHEDULE_PREFERENCE", "SLEEP_PREFERENCES", "sleep_schedule_preference", "baby_id"),
    ];

    public sealed record Parsed(object? Value, NpgsqlDbType DbType);

    /// <summary>Converte o JSON do cliente para parâmetro tipado; null em erro de tipo.</summary>
    public static bool TryParse(FieldSpec f, JsonElement el, out object? value, out NpgsqlDbType type)
    {
        value = null;
        type = f.Kind switch
        {
            FieldKind.Text => NpgsqlDbType.Text,
            FieldKind.Timestamp => NpgsqlDbType.TimestampTz,
            FieldKind.Number => NpgsqlDbType.Numeric,
            _ => NpgsqlDbType.Uuid,
        };
        if (el.ValueKind == JsonValueKind.Null) { value = DBNull.Value; return f.Nullable; }
        switch (f.Kind)
        {
            case FieldKind.Text:
                if (el.ValueKind != JsonValueKind.String) return false;
                value = el.GetString();
                return true;
            case FieldKind.Timestamp:
                if (el.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(el.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)) return false;
                value = dto.UtcDateTime;
                return true;
            case FieldKind.Number:
                if (el.ValueKind != JsonValueKind.Number || !el.TryGetDecimal(out var d)) return false;
                value = d;
                return true;
            default:
                if (el.ValueKind != JsonValueKind.String || !Guid.TryParse(el.GetString(), out var g)) return false;
                value = g;
                return true;
        }
    }
}
