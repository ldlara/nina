using System.Text.Json.Nodes;
using Nina.Tracking.Domain;

namespace Nina.Tracking.Reads;

/// <summary>Autoria exibível (<c>UserRef</c>): só <c>id</c> e nome, nunca e-mail. Autor que saiu do bebê ou anonimizado cai em rótulos neutros.</summary>
internal sealed class Authors(IReadOnlyDictionary<Guid, string> names)
{
    /// <summary>Autoria anonimizada (<c>created_by</c> nulo após <c>scrub_user_personal_data</c>): o contrato exige um objeto, então vai um id nulo (UUID zero).</summary>
    public static readonly Guid Anonymous = Guid.Empty;

    public JsonObject Ref(Guid? id) => new()
    {
        ["id"] = (id ?? Anonymous).ToString("D"),
        ["display_name"] = id is { } known && names.TryGetValue(known, out var name) ? name : string.Empty,
    };
}

/// <summary>Linha do banco (<c>to_jsonb</c>) para o DTO do contrato (<c>Event</c>, <c>WakeEvent</c>, <c>SleepPreferences</c>, <c>Baby</c>).</summary>
internal static class EntityMapper
{
    public static JsonObject Event(EntitySpec spec, JsonObject row, Authors authors, int? nightAwakenings)
    {
        var start = Wire.ReadInstant(row[spec.StartField ?? "occurred_at"]);
        var end = spec.EndField is null ? null : Wire.ReadInstant(row[spec.EndField]);
        var dto = new JsonObject
        {
            ["event_type"] = spec.EventType,
            ["id"] = Wire.ReadGuid(row["id"])!.Value.ToString("D"),
            ["baby_id"] = Wire.ReadGuid(row["baby_id"])!.Value.ToString("D"),
            ["version"] = Wire.ReadLong(row["version"]),
            ["tz"] = Wire.ReadString(row["tz"]),
            ["created_at"] = Wire.Instant(Wire.RequireInstant(row["created_at"])),
            ["updated_at"] = Wire.Instant(Wire.RequireInstant(row["updated_at"])),
            ["created_by"] = authors.Ref(Wire.ReadGuid(row["created_by"])),
            ["last_modified_by"] = authors.Ref(Wire.ReadGuid(row["last_modified_by"])),
        };

        switch (spec.Type)
        {
            case EntityCatalog.SleepSession:
                dto["sleep_type"] = Wire.ReadString(row["sleep_type"]);
                dto["start_at"] = Wire.InstantOrNull(start);
                dto["end_at"] = Wire.InstantOrNull(end);
                dto["duration_seconds"] = Duration(start, end);
                dto["method_or_place"] = Wire.ReadString(row["method_or_place"]);
                dto["notes"] = Wire.ReadString(row["notes"]);
                dto["source"] = Wire.ReadString(row["source"]);
                dto["night_awakenings"] = Wire.Number(nightAwakenings);
                break;
            case EntityCatalog.FeedingSession:
                dto["feeding_type"] = Wire.ReadString(row["feeding_type"]);
                dto["start_at"] = Wire.InstantOrNull(start);
                dto["end_at"] = Wire.InstantOrNull(end);
                dto["duration_seconds"] = Duration(start, end);
                dto["side"] = Wire.ReadString(row["side"]);
                dto["volume_ml"] = Wire.Number(Wire.ReadLong(row["volume_ml"]));
                dto["milk_type"] = Wire.ReadString(row["milk_type"]);
                dto["notes"] = Wire.ReadString(row["notes"]);
                break;
            case EntityCatalog.PumpingSession:
                dto["start_at"] = Wire.InstantOrNull(start);
                dto["end_at"] = Wire.InstantOrNull(end);
                dto["duration_seconds"] = Duration(start, end);
                dto["volume_ml"] = Wire.Number(Wire.ReadLong(row["volume_ml"]));
                dto["side"] = Wire.ReadString(row["side"]);
                break;
            case EntityCatalog.DiaperEvent:
                dto["occurred_at"] = Wire.InstantOrNull(start);
                dto["diaper_type"] = Wire.ReadString(row["diaper_type"]);
                dto["notes"] = Wire.ReadString(row["notes"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(spec));
        }

        return dto;
    }

    public static JsonObject Wake(JsonObject row) => new()
    {
        ["id"] = Wire.ReadGuid(row["id"])!.Value.ToString("D"),
        ["baby_id"] = Wire.ReadGuid(row["baby_id"])!.Value.ToString("D"),
        ["sleep_session_id"] = Wire.ReadGuid(row["sleep_session_id"])!.Value.ToString("D"),
        ["started_at"] = Wire.Instant(Wire.RequireInstant(row["started_at"])),
        ["ended_at"] = Wire.InstantOrNull(Wire.ReadInstant(row["ended_at"])),
        ["duration_seconds"] = Wire.Number(Wire.ReadLong(row["duration_seconds"])),
        ["source"] = Wire.ReadString(row["source"]),
        ["version"] = Wire.ReadLong(row["version"]),
        ["created_at"] = Wire.Instant(Wire.RequireInstant(row["created_at"])),
        ["updated_at"] = Wire.Instant(Wire.RequireInstant(row["updated_at"])),
    };

    public static JsonObject SleepPreferences(JsonObject row) => new()
    {
        ["baby_id"] = Wire.ReadGuid(row["baby_id"])!.Value.ToString("D"),
        ["target_nap_count"] = Wire.Number(Wire.ReadLong(row["target_nap_count"])),
        ["bedtime_from"] = Wire.ReadTimeOfDay(row["bedtime_from"]),
        ["bedtime_to"] = Wire.ReadTimeOfDay(row["bedtime_to"]),
        ["version"] = Wire.ReadLong(row["version"]),
        ["updated_at"] = Wire.Instant(Wire.RequireInstant(row["updated_at"])),
    };

    /// <summary>
    /// <c>Baby</c> do feed. <paramref name="row"/> é <c>to_jsonb(baby)</c> mais <c>my_role</c>, <c>local_today</c> (data civil no fuso do bebê) e
    /// <c>age_calc</c> (<c>nina.age_calculation</c>). A idade corrigida nunca é persistida (ADR-0009); a decomposição em semanas e meses é só para exibição.
    /// </summary>
    public static JsonObject Baby(JsonObject row)
    {
        var birth = DateOnly.Parse(Wire.ReadString(row["birth_date"])!, System.Globalization.CultureInfo.InvariantCulture);
        var due = Wire.ReadString(row["due_date"]) is { } d ? DateOnly.Parse(d, System.Globalization.CultureInfo.InvariantCulture) : (DateOnly?)null;
        var today = DateOnly.Parse(Wire.ReadString(row["local_today"])!, System.Globalization.CultureInfo.InvariantCulture);
        var calc = (JsonObject)row["age_calc"]!;
        var chronologicalDays = (int)Wire.ReadLong(calc["chronological_days"])!.Value;
        var correctedDays = Wire.ReadLong(calc["corrected_days"]);
        var applied = calc["correction_applied"]?.GetValue<bool>() ?? false;

        var age = new JsonObject
        {
            ["as_of"] = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["chronological"] = AgeValue(chronologicalDays, WholeMonths(birth, today)),
            ["corrected"] = applied && correctedDays is { } cd && due is { } dueDate
                ? AgeValue((int)cd, today >= dueDate ? WholeMonths(dueDate, today) : 0)
                : null,
        };

        return new JsonObject
        {
            ["id"] = Wire.ReadGuid(row["id"])!.Value.ToString("D"),
            ["display_name"] = Wire.ReadString(row["display_name"]),
            ["birth_date"] = birth.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["due_date"] = due?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["sex"] = Wire.ReadString(row["sex"]),
            ["timezone"] = Wire.ReadString(row["timezone"]),
            ["photo_ref"] = Wire.ReadString(row["photo_ref"]),
            ["my_role"] = Wire.ReadString(row["my_role"]),
            ["age"] = age,
            ["age_calculation"] = new JsonObject
            {
                ["chronological_days"] = chronologicalDays,
                ["corrected_days"] = applied ? correctedDays : null,
                ["correction_applied"] = applied,
            },
            ["version"] = Wire.ReadLong(row["version"]),
            ["created_at"] = Wire.Instant(Wire.RequireInstant(row["created_at"])),
            ["updated_at"] = Wire.Instant(Wire.RequireInstant(row["updated_at"])),
        };
    }

    private static JsonObject AgeValue(int days, int months) => new()
    {
        ["days"] = days,
        ["weeks"] = days / 7,
        ["months"] = months,
    };

    private static int WholeMonths(DateOnly from, DateOnly to)
    {
        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        if (to.Day < from.Day)
        {
            months--;
        }

        return Math.Max(months, 0);
    }

    private static JsonValue? Duration(DateTimeOffset? start, DateTimeOffset? end) =>
        start is { } s && end is { } e ? JsonValue.Create((long)Math.Floor((e - s).TotalSeconds)) : null;
}
