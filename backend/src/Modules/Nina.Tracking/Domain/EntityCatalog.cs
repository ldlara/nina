namespace Nina.Tracking.Domain;

internal enum FieldKind
{
    Instant,
    Text,
    TimeZone,
    Int,
    Enum,
    Guid,
}

/// <summary>Campo de uma entidade sincronizável. O nome do contrato é igual ao da coluna (snake_case).</summary>
internal sealed record FieldSpec(
    string Name,
    FieldKind Kind,
    bool Nullable = false,
    int MaxLength = 0,
    IReadOnlyList<string>? Values = null,
    int Min = 0,
    int Max = 0);

/// <summary>
/// Descrição declarativa de uma entidade do push. Nomes de tabela e coluna vêm só daqui (constantes): nada que o cliente envia
/// vira identificador SQL.
/// </summary>
internal sealed class EntitySpec
{
    public required string Type { get; init; }

    public required string Table { get; init; }

    /// <summary><c>event_type</c> do DTO (nulo em <c>WAKE_EVENT</c>, que não é um <c>Event</c>).</summary>
    public string? EventType { get; init; }

    /// <summary>Campos graváveis no CREATE (<c>data</c>).</summary>
    public required IReadOnlyList<FieldSpec> Fields { get; init; }

    public required IReadOnlySet<string> CreateRequired { get; init; }

    /// <summary>Campos aceitos em <c>UPDATE</c> (<c>EventPatch</c> restrito à entidade).</summary>
    public required IReadOnlySet<string> Patchable { get; init; }

    /// <summary>Grupos atômicos de campos: a unidade do last-write-wins quando há invariante entre eles (R-12).</summary>
    public IReadOnlyList<string[]> Groups { get; init; } = [];

    public string? StartField { get; init; }

    public string? EndField { get; init; }

    public bool EndRequired { get; init; }

    public FieldSpec? Field(string name) => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    public string[]? GroupOf(string field) => Groups.FirstOrDefault(g => g.Contains(field, StringComparer.Ordinal));
}

internal static class EntityCatalog
{
    public const string SleepSession = "SLEEP_SESSION";
    public const string FeedingSession = "FEEDING_SESSION";
    public const string PumpingSession = "PUMPING_SESSION";
    public const string DiaperEvent = "DIAPER_EVENT";
    public const string WakeEvent = "WAKE_EVENT";

    public static readonly IReadOnlyList<string> BreastSides = ["LEFT", "RIGHT", "BOTH"];

    public static readonly IReadOnlyList<string> MilkTypes = ["BREAST_MILK", "FORMULA", "MIXED", "OTHER", "UNSPECIFIED"];

    public static readonly IReadOnlyList<string> FeedingTypes = ["BREASTFEEDING", "BOTTLE", "SOLID", "OTHER"];

    public static readonly IReadOnlyList<string> DiaperTypes = ["WET", "DIRTY", "MIXED", "DRY", "UNSPECIFIED"];

    private static readonly FieldSpec StartAt = new("start_at", FieldKind.Instant);
    private static readonly FieldSpec Tz = new("tz", FieldKind.TimeZone);
    private static readonly FieldSpec Notes = new("notes", FieldKind.Text, Nullable: true, MaxLength: 500);

    private static readonly EntitySpec Sleep = new()
    {
        Type = SleepSession,
        Table = "sleep_session",
        EventType = "SLEEP",
        Fields =
        [
            new("sleep_type", FieldKind.Enum, Values: ["NAP", "NIGHT"]),
            StartAt,
            new("end_at", FieldKind.Instant, Nullable: true),
            Tz,
            new("method_or_place", FieldKind.Text, Nullable: true, MaxLength: 80),
            Notes,
            new("source", FieldKind.Enum, Values: ["TIMER", "MANUAL"]),
        ],
        CreateRequired = new HashSet<string> { "sleep_type", "start_at", "tz", "source" },
        Patchable = new HashSet<string> { "sleep_type", "start_at", "end_at", "tz", "method_or_place", "notes" },
        Groups = [["start_at", "end_at"]],
        StartField = "start_at",
        EndField = "end_at",
    };

    private static readonly EntitySpec Feeding = new()
    {
        Type = FeedingSession,
        Table = "feeding_session",
        EventType = "FEEDING",
        Fields =
        [
            new("feeding_type", FieldKind.Enum, Values: FeedingTypes),
            StartAt,
            new("end_at", FieldKind.Instant, Nullable: true),
            Tz,
            new("side", FieldKind.Enum, Nullable: true, Values: BreastSides),
            new("volume_ml", FieldKind.Int, Nullable: true, Min: 1, Max: 5000),
            new("milk_type", FieldKind.Enum, Nullable: true, Values: MilkTypes),
            Notes,
        ],
        CreateRequired = new HashSet<string> { "feeding_type", "start_at", "tz" },
        Patchable = new HashSet<string> { "start_at", "end_at", "tz", "notes", "side", "volume_ml", "milk_type" },
        Groups = [["start_at", "end_at"]],
        StartField = "start_at",
        EndField = "end_at",
    };

    private static readonly EntitySpec Pumping = new()
    {
        Type = PumpingSession,
        Table = "pumping_session",
        EventType = "PUMPING",
        Fields =
        [
            StartAt,
            new("end_at", FieldKind.Instant),
            Tz,
            new("volume_ml", FieldKind.Int, Nullable: true, Min: 1, Max: 5000),
            new("side", FieldKind.Enum, Nullable: true, Values: BreastSides),
        ],
        CreateRequired = new HashSet<string> { "start_at", "end_at", "tz" },
        Patchable = new HashSet<string> { "start_at", "end_at", "tz", "volume_ml", "side" },
        Groups = [["start_at", "end_at"]],
        StartField = "start_at",
        EndField = "end_at",
        EndRequired = true,
    };

    private static readonly EntitySpec Diaper = new()
    {
        Type = DiaperEvent,
        Table = "diaper_event",
        EventType = "DIAPER",
        Fields =
        [
            new("occurred_at", FieldKind.Instant),
            new("diaper_type", FieldKind.Enum, Values: DiaperTypes),
            Tz,
            Notes,
        ],
        CreateRequired = new HashSet<string> { "occurred_at", "diaper_type", "tz" },
        Patchable = new HashSet<string> { "occurred_at", "tz", "diaper_type", "notes" },
    };

    private static readonly EntitySpec Wake = new()
    {
        Type = WakeEvent,
        Table = "wake_event",
        Fields =
        [
            new("sleep_session_id", FieldKind.Guid),
            new("started_at", FieldKind.Instant),
            new("ended_at", FieldKind.Instant),
            new("source", FieldKind.Enum, Values: ["MANUAL", "INFERRED", "IMPORT"]),
        ],
        CreateRequired = new HashSet<string> { "sleep_session_id", "started_at", "ended_at" },
        Patchable = new HashSet<string> { "started_at", "ended_at", "source" },
        Groups = [["started_at", "ended_at"]],
        StartField = "started_at",
        EndField = "ended_at",
        EndRequired = true,
    };

    public static IReadOnlyList<EntitySpec> All { get; } = [Sleep, Feeding, Pumping, Diaper, Wake];

    public static EntitySpec? ByType(string? type) => All.FirstOrDefault(s => string.Equals(s.Type, type, StringComparison.Ordinal));

    public static EntitySpec For(string type) => ByType(type) ?? throw new ArgumentOutOfRangeException(nameof(type));

    /// <summary>Posição do tipo no snapshot (BABY primeiro; despertares depois das sessões de sono).</summary>
    public static int SnapshotRank(string feedType) => feedType switch
    {
        "BABY" => 0,
        "SLEEP_PREFERENCES" => 1,
        SleepSession => 2,
        WakeEvent => 3,
        FeedingSession => 4,
        PumpingSession => 5,
        DiaperEvent => 6,
        _ => 99,
    };
}
