using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nina.SleepIntelligence.Tests.Support;

internal static class Tz
{
    public const string SaoPaulo = "America/Sao_Paulo";
    public const string Lisbon = "Europe/Lisbon";
    public const string NewYork = "America/New_York";
    public const string Kolkata = "Asia/Kolkata";
    public const string LordHowe = "Australia/Lord_Howe";

    public static TimeZoneInfo Zone(string id) => TimeZones.TryResolve(id) ?? throw new InvalidOperationException(id);

    /// <summary>Instante UTC de uma hora local de parede no fuso informado.</summary>
    public static DateTimeOffset At(string tz, int y, int mo, int d, int h, int mi = 0) =>
        TimeZones.ToUtc(new DateOnly(y, mo, d), (h * 60) + mi, Zone(tz));
}

internal static class Engines
{
    public static ReferenceTable Table { get; } = ReferenceTable.LoadDefault();

    /// <summary>Conteúdo bruto do arquivo JSON versionado embutido no assembly.</summary>
    public static string TableJson()
    {
        using var stream = typeof(ReferenceTable).Assembly.GetManifestResourceStream("Nina.SleepIntelligence.Data.sleep-reference-table.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static SleepPredictionEngine Create(SleepEngineOptions? options = null) => new(Table, options);
}

/// <summary>Gerador determinístico (semente fixa) de "bebês sintéticos" (test-strategy 4.6 e 10.8).</summary>
internal sealed class SyntheticBaby
{
    private int _seq;

    public SyntheticBaby(string tz = Tz.SaoPaulo)
    {
        TimeZoneId = tz;
    }

    public string TimeZoneId { get; }

    public List<SleepRecord> Records { get; } = [];

    /// <summary>
    /// Gera <paramref name="days"/> dias consecutivos: despertar, N sonecas separadas por wake windows, noite até o despertar
    /// do dia seguinte (a última noite termina na manhã seguinte ao último dia). Jitter em minutos (±), semente fixa.
    /// </summary>
    public SyntheticBaby Days(
        DateOnly firstDay,
        int days,
        int naps,
        int wakeMinutes,
        int napMinutes,
        int preBedWakeMinutes,
        int bedtimeMinuteOfDay,
        int jitter,
        int seed,
        int wakeUpMinuteOfDay = (6 * 60) + 30)
    {
        var rnd = new Random(seed);
        var zone = Tz.Zone(TimeZoneId);
        int J() => jitter == 0 ? 0 : rnd.Next(-jitter, jitter + 1);

        var cursor = TimeZones.ToUtc(firstDay, wakeUpMinuteOfDay + J(), zone);
        for (var i = 0; i < days; i++)
        {
            var day = firstDay.AddDays(i);
            for (var n = 0; n < naps; n++)
            {
                var start = cursor.AddMinutes(wakeMinutes + J());
                var end = start.AddMinutes(napMinutes + J());
                Add(start, end, SleepKind.Nap);
                cursor = end;
            }

            var bedtime = TimeZones.ToUtc(day, bedtimeMinuteOfDay + J(), zone);
            var minBed = cursor.AddMinutes(preBedWakeMinutes - Math.Abs(jitter));
            if (bedtime < minBed)
            {
                bedtime = minBed;
            }

            var wake = TimeZones.ToUtc(day.AddDays(1), wakeUpMinuteOfDay + J(), zone);
            Add(bedtime, wake, SleepKind.Night);
            cursor = wake;
        }

        return this;
    }

    /// <summary>Instante em que o bebê acordou da última sessão fechada.</summary>
    public DateTimeOffset LastEnd => Records.Where(r => r.EndAt is not null).Max(r => r.EndAt!.Value);

    public SyntheticBaby Add(DateTimeOffset start, DateTimeOffset? end, SleepKind kind, string? tz = null)
    {
        Records.Add(new SleepRecord(Guid.Parse($"00000000-0000-0000-0000-{++_seq:D12}"), start, end, kind, tz ?? TimeZoneId));
        return this;
    }
}

/// <summary>Serialização estável para comparações e para os arquivos golden.</summary>
internal static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions { WriteIndented = true };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

internal static class Req
{
    /// <summary>Requisição com bebê de <paramref name="ageDays"/> dias (cronológicos) na data local de <paramref name="asOf"/>.</summary>
    public static SleepPredictionRequest For(
        IEnumerable<SleepRecord> history,
        int ageDays,
        DateTimeOffset asOf,
        string tz = Tz.SaoPaulo,
        int? dueOffsetDays = null,
        SleepPreferences? prefs = null)
    {
        var today = TimeZones.LocalDate(asOf, Tz.Zone(tz));
        var birth = today.AddDays(-ageDays);
        return new SleepPredictionRequest(
            new BabyProfile(birth, dueOffsetDays is { } d ? birth.AddDays(d) : null, tz),
            history.ToList(),
            prefs);
    }

    public static SleepPredictionItem Nap(this SleepPredictionResult r) => r.Predictions.Single(p => p.Kind == PredictionKind.NextNap);

    public static SleepPredictionItem Bed(this SleepPredictionResult r) => r.Predictions.Single(p => p.Kind == PredictionKind.Bedtime);

    public static int Local(this DateTimeOffset t, string tz = Tz.SaoPaulo) => TimeZones.LocalMinuteOfDay(t, Tz.Zone(tz));
}
