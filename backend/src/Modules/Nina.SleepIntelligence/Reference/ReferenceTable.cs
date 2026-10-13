using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nina.SleepIntelligence;

/// <summary>Faixa em minutos (mín ≤ típico ≤ máx).</summary>
public sealed record MinutesRange(int Min, int Typical, int Max);

/// <summary>Faixa de contagem de sonecas; <see cref="Typical"/> nulo = sem número fixo (ex.: neonatal).</summary>
public sealed record CountRange(int Min, int? Typical, int Max);

/// <summary>Horários de bedtime em minutos desde 00:00 local.</summary>
public sealed record BedtimeRange(int EarliestMinuteOfDay, int TypicalMinuteOfDay, int LatestMinuteOfDay);

/// <summary>Referência já resolvida para uma idade (com suavização entre faixas, SE-CS-06).</summary>
public sealed record AgeReference(
    string BandId,
    int AgeDaysFrom,
    int AgeDaysTo,
    MinutesRange NapWakeMinutes,
    MinutesRange PreBedtimeWakeMinutes,
    CountRange NapCount,
    MinutesRange NapDurationMinutes,
    BedtimeRange? Bedtime);

/// <summary>Limites globais de plausibilidade (D-21), usados também para validar preferências.</summary>
public sealed record PlausibilityLimits(int BedtimeEarliestMinuteOfDay, int BedtimeLatestMinuteOfDay, int MaxNapCount);

/// <summary>
/// Tabela de referência por idade, carregada de JSON versionado (dado configurável, RF-011-A9). Nenhum valor clínico
/// está no código. O conteúdo atual é RASCUNHO e exige validação de especialista (ADR-0004, gate humano de BE-005).
/// </summary>
public sealed class ReferenceTable
{
    private const string ResourceName = "Nina.SleepIntelligence.Data.sleep-reference-table.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IReadOnlyList<BandData> _bands;

    private ReferenceTable(
        string version,
        string header,
        string status,
        string hash,
        int transitionDays,
        int maxAgeDays,
        PlausibilityLimits plausibility,
        IReadOnlyList<BandData> bands)
    {
        Version = version;
        Header = header;
        Status = status;
        Hash = hash;
        TransitionDays = transitionDays;
        MaxAgeDays = maxAgeDays;
        Plausibility = plausibility;
        _bands = bands;
    }

    public string Version { get; }

    public string Header { get; }

    public string Status { get; }

    /// <summary>Hash (SHA-256, 16 hex) do conteúdo do arquivo, registrado em cada previsão (test-strategy 4.6).</summary>
    public string Hash { get; }

    public int TransitionDays { get; }

    /// <summary>Idade efetiva (dias) a partir da qual não há referência (exclusivo).</summary>
    public int MaxAgeDays { get; }

    public PlausibilityLimits Plausibility { get; }

    /// <summary>Verdadeiro enquanto o cabeçalho marca o arquivo como RASCUNHO.</summary>
    public bool IsDraft => Header.Contains("RASCUNHO", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<AgeReference> Bands => _bands.Select(b => b.ToReference()).ToList();

    /// <summary>Carrega a tabela embutida no assembly.</summary>
    public static ReferenceTable LoadDefault()
    {
        using var stream = typeof(ReferenceTable).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Recurso embutido não encontrado: {ResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Carrega de um arquivo (borda de I/O; a biblioteca em si não lê disco).</summary>
    public static ReferenceTable LoadFromFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Interpreta e valida o JSON (cobertura contínua, sem lacunas/sobreposição, mín ≤ típico ≤ máx).</summary>
    public static ReferenceTable Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Dto dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions) ?? throw new FormatException("JSON vazio.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Tabela de referência inválida: " + ex.Message, ex);
        }

        if (string.IsNullOrWhiteSpace(dto.Header))
        {
            throw new FormatException("Tabela de referência sem cabeçalho (_header).");
        }

        if (string.IsNullOrWhiteSpace(dto.Version))
        {
            throw new FormatException("Tabela de referência sem version.");
        }

        if (dto.Bands is null || dto.Bands.Count == 0)
        {
            throw new FormatException("Tabela de referência sem faixas.");
        }

        var transition = dto.TransitionDays;
        if (transition < 0 || transition % 2 != 0)
        {
            throw new FormatException("transition_days deve ser par e não negativo.");
        }

        var plaus = dto.Plausibility ?? throw new FormatException("plausibility ausente.");
        var limits = new PlausibilityLimits(ParseTime(plaus.BedtimeEarliest), ParseTime(plaus.BedtimeLatest), plaus.MaxNapCount);
        if (limits.BedtimeEarliestMinuteOfDay >= limits.BedtimeLatestMinuteOfDay || limits.MaxNapCount < 1)
        {
            throw new FormatException("plausibility inconsistente.");
        }

        var bands = new List<BandData>();
        var expectedFrom = 0;
        foreach (var b in dto.Bands)
        {
            var band = BandData.Create(b);
            if (band.From != expectedFrom)
            {
                throw new FormatException($"Faixa '{band.Id}' começa em {band.From}, esperado {expectedFrom} (lacuna ou sobreposição).");
            }

            if (band.To <= band.From || (transition > 0 && band.To - band.From < transition))
            {
                throw new FormatException($"Faixa '{band.Id}' inválida (intervalo vazio ou menor que transition_days).");
            }

            expectedFrom = band.To;
            bands.Add(band);
        }

        if (dto.MaxAgeDays != expectedFrom)
        {
            throw new FormatException("max_age_days deve coincidir com o fim da última faixa.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.ReplaceLineEndings("\n")))).ToLowerInvariant()[..16];
        return new ReferenceTable(dto.Version, dto.Header, dto.Status ?? string.Empty, hash, transition, dto.MaxAgeDays, limits, bands);
    }

    /// <summary>Referência para a idade efetiva em dias; nulo se fora da cobertura.</summary>
    public AgeReference? Resolve(int effectiveAgeDays)
    {
        if (effectiveAgeDays < 0 || effectiveAgeDays >= MaxAgeDays)
        {
            return null;
        }

        var k = 0;
        while (k < _bands.Count - 1 && effectiveAgeDays >= _bands[k].To)
        {
            k++;
        }

        var band = _bands[k];
        var half = TransitionDays / 2;
        if (half > 0 && k + 1 < _bands.Count && effectiveAgeDays >= band.To - half)
        {
            var w = (effectiveAgeDays - (band.To - half)) * 50 / half;
            return Blend(band, _bands[k + 1], w, band);
        }

        if (half > 0 && k > 0 && effectiveAgeDays < band.From + half)
        {
            var w = 50 + ((effectiveAgeDays - band.From) * 50 / half);
            return Blend(_bands[k - 1], band, w, band);
        }

        return band.ToReference();
    }

    private static AgeReference Blend(BandData a, BandData b, int weightB, BandData identity)
    {
        static int Mix(int x, int y, int w) => ((x * (100 - w)) + (y * w) + 50) / 100;

        static MinutesRange MixRange(MinutesRange x, MinutesRange y, int w) =>
            new(Mix(x.Min, y.Min, w), Mix(x.Typical, y.Typical, w), Mix(x.Max, y.Max, w));

        var usesB = weightB >= 50;
        BedtimeRange? bedtime;
        if (a.Bedtime is not null && b.Bedtime is not null)
        {
            bedtime = new BedtimeRange(
                Mix(a.Bedtime.EarliestMinuteOfDay, b.Bedtime.EarliestMinuteOfDay, weightB),
                Mix(a.Bedtime.TypicalMinuteOfDay, b.Bedtime.TypicalMinuteOfDay, weightB),
                Mix(a.Bedtime.LatestMinuteOfDay, b.Bedtime.LatestMinuteOfDay, weightB));
        }
        else
        {
            bedtime = usesB ? b.Bedtime : a.Bedtime;
        }

        var naps = new CountRange(
            Math.Min(a.NapCount.Min, b.NapCount.Min),
            usesB ? b.NapCount.Typical : a.NapCount.Typical,
            Math.Max(a.NapCount.Max, b.NapCount.Max));

        return new AgeReference(
            identity.Id,
            identity.From,
            identity.To,
            MixRange(a.NapWake, b.NapWake, weightB),
            MixRange(a.PreBedWake, b.PreBedWake, weightB),
            naps,
            MixRange(a.NapDuration, b.NapDuration, weightB),
            bedtime);
    }

    internal static int ParseTime(string? value)
    {
        if (value is null || !TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            throw new FormatException($"Horário inválido (esperado HH:mm): '{value}'.");
        }

        return (t.Hour * 60) + t.Minute;
    }

    private sealed record BandData(
        string Id,
        int From,
        int To,
        MinutesRange NapWake,
        MinutesRange PreBedWake,
        CountRange NapCount,
        MinutesRange NapDuration,
        BedtimeRange? Bedtime)
    {
        public static BandData Create(BandDto b)
        {
            var id = string.IsNullOrWhiteSpace(b.Id) ? throw new FormatException("Faixa sem id.") : b.Id;
            var bedtime = b.BedtimeLocal is null
                ? null
                : new BedtimeRange(ParseTime(b.BedtimeLocal.Earliest), ParseTime(b.BedtimeLocal.Typical), ParseTime(b.BedtimeLocal.Latest));
            if (bedtime is not null && !(bedtime.EarliestMinuteOfDay <= bedtime.TypicalMinuteOfDay && bedtime.TypicalMinuteOfDay <= bedtime.LatestMinuteOfDay))
            {
                throw new FormatException($"Faixa '{id}': bedtime deve obedecer earliest ≤ typical ≤ latest.");
            }

            var nap = Range(id, b.NapWakeMinutes, "nap_wake_minutes");
            var pre = Range(id, b.PreBedtimeWakeMinutes, "pre_bedtime_wake_minutes");
            var dur = Range(id, b.NapDurationMinutes, "nap_duration_minutes");
            var count = b.NapCount ?? throw new FormatException($"Faixa '{id}': nap_count ausente.");
            if (count.Min < 0 || count.Max < count.Min || (count.Typical is { } t && (t < count.Min || t > count.Max)))
            {
                throw new FormatException($"Faixa '{id}': nap_count inconsistente.");
            }

            return new BandData(id, b.AgeDaysFrom, b.AgeDaysTo, nap, pre, new CountRange(count.Min, count.Typical, count.Max), dur, bedtime);
        }

        public AgeReference ToReference() => new(Id, From, To, NapWake, PreBedWake, NapCount, NapDuration, Bedtime);

        private static MinutesRange Range(string id, RangeDto? r, string name)
        {
            if (r is null || r.Min <= 0 || r.Min > r.Typical || r.Typical > r.Max)
            {
                throw new FormatException($"Faixa '{id}': {name} deve obedecer 0 < min ≤ typical ≤ max.");
            }

            return new MinutesRange(r.Min, r.Typical, r.Max);
        }
    }

    private sealed class Dto
    {
        [JsonPropertyName("_header")]
        public string? Header { get; set; }

        public string? Version { get; set; }

        public string? Status { get; set; }

        public int TransitionDays { get; set; }

        public int MaxAgeDays { get; set; }

        public PlausibilityDto? Plausibility { get; set; }

        public List<BandDto>? Bands { get; set; }
    }

    private sealed class PlausibilityDto
    {
        public string? BedtimeEarliest { get; set; }

        public string? BedtimeLatest { get; set; }

        public int MaxNapCount { get; set; }
    }

    private sealed class BandDto
    {
        public string? Id { get; set; }

        public int AgeDaysFrom { get; set; }

        public int AgeDaysTo { get; set; }

        public RangeDto? NapWakeMinutes { get; set; }

        public RangeDto? PreBedtimeWakeMinutes { get; set; }

        public CountDto? NapCount { get; set; }

        public RangeDto? NapDurationMinutes { get; set; }

        public BedtimeDto? BedtimeLocal { get; set; }
    }

    private sealed class RangeDto
    {
        public int Min { get; set; }

        public int Typical { get; set; }

        public int Max { get; set; }
    }

    private sealed class CountDto
    {
        public int Min { get; set; }

        public int? Typical { get; set; }

        public int Max { get; set; }
    }

    private sealed class BedtimeDto
    {
        public string? Earliest { get; set; }

        public string? Typical { get; set; }

        public string? Latest { get; set; }
    }
}
