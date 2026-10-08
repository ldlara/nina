using System.Text.Json.Nodes;
using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

public sealed class ReferenceTableTests
{
    private static readonly ReferenceTable Table = Engines.Table;

    [Fact]
    public void Default_table_is_a_versioned_draft_with_the_clinical_validation_header()
    {
        Assert.Equal("RASCUNHO — requer validação por especialista clínico", Table.Header);
        Assert.True(Table.IsDraft);
        Assert.False(string.IsNullOrWhiteSpace(Table.Version));
        Assert.Equal(16, Table.Hash.Length);
        Assert.Equal("DRAFT_REQUIRES_CLINICAL_VALIDATION", Table.Status);
    }

    [Fact]
    public void Shipped_file_marks_every_value_as_provisional()
    {
        var root = JsonNode.Parse(Engines.TableJson())!;
        Assert.True(root["provisional"]!.GetValue<bool>());
        Assert.StartsWith("RASCUNHO", root["_header"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("PROVISÓRIOS", root["_notice"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Bands_cover_ages_continuously_without_gaps_or_overlaps()
    {
        var bands = Table.Bands;
        Assert.Equal(0, bands[0].AgeDaysFrom);
        for (var i = 1; i < bands.Count; i++)
        {
            Assert.Equal(bands[i - 1].AgeDaysTo, bands[i].AgeDaysFrom);
        }

        Assert.Equal(Table.MaxAgeDays, bands[^1].AgeDaysTo);
        for (var age = 0; age < Table.MaxAgeDays; age++)
        {
            Assert.NotNull(Table.Resolve(age));
        }

        Assert.Null(Table.Resolve(Table.MaxAgeDays));
        Assert.Null(Table.Resolve(-1));
    }

    [Fact]
    public void Every_band_is_ordered_min_typical_max()
    {
        foreach (var b in Table.Bands)
        {
            foreach (var r in new[] { b.NapWakeMinutes, b.PreBedtimeWakeMinutes, b.NapDurationMinutes })
            {
                Assert.True(r.Min <= r.Typical && r.Typical <= r.Max, b.BandId);
            }

            if (b.Bedtime is { } bt)
            {
                Assert.True(bt.EarliestMinuteOfDay <= bt.TypicalMinuteOfDay && bt.TypicalMinuteOfDay <= bt.LatestMinuteOfDay, b.BandId);
            }
        }
    }

    [Fact]
    public void Neonatal_band_has_no_fixed_nap_count_or_bedtime()
    {
        var r = Table.Resolve(10)!;
        Assert.Null(r.NapCount.Typical);
        Assert.Null(r.Bedtime);
    }

    [Fact]
    public void Transition_between_bands_is_smooth_without_absurd_jumps()
    {
        var maxStep = 0;
        for (var age = 1; age < Table.MaxAgeDays; age++)
        {
            var a = Table.Resolve(age - 1)!;
            var b = Table.Resolve(age)!;
            maxStep = Math.Max(maxStep, Math.Abs(b.NapWakeMinutes.Typical - a.NapWakeMinutes.Typical));
        }

        // Sem a suavização o salto no limite de faixa chegaria a 30+ min; com ela, no máximo poucos minutos por dia.
        Assert.True(maxStep <= 8, $"salto diário máximo = {maxStep}");
    }

    [Fact]
    public void Transition_at_boundary_is_halfway_between_the_two_bands()
    {
        var boundary = Table.Bands[3].AgeDaysTo; // 240
        var left = Table.Bands[3].NapWakeMinutes.Typical;
        var right = Table.Bands[4].NapWakeMinutes.Typical;
        var at = Table.Resolve(boundary)!.NapWakeMinutes.Typical;
        Assert.InRange(at, Math.Min(left, right), Math.Max(left, right));
        Assert.InRange(Math.Abs(at - ((left + right) / 2)), 0, 2);
        // Longe da fronteira, o valor da faixa permanece.
        Assert.Equal(left, Table.Resolve(Table.Bands[3].AgeDaysFrom + 30)!.NapWakeMinutes.Typical);
    }

    [Fact]
    public void Hash_changes_when_content_changes_and_is_stable_otherwise()
    {
        var json = Engines.TableJson();
        Assert.Equal(Table.Hash, ReferenceTable.Parse(json).Hash);
        Assert.NotEqual(Table.Hash, ReferenceTable.Parse(json.Replace("\"typical\": 45", "\"typical\": 46", StringComparison.Ordinal)).Hash);
    }

    [Theory]
    [InlineData("\"age_days_from\": 42", "\"age_days_from\": 50")] // lacuna
    [InlineData("\"age_days_from\": 91", "\"age_days_from\": 80")] // sobreposição
    [InlineData("\"min\": 30, \"typical\": 45, \"max\": 60", "\"min\": 70, \"typical\": 45, \"max\": 60")] // min > typical
    [InlineData("\"_header\"", "\"_h\"")] // sem cabeçalho
    [InlineData("\"max_age_days\": 1095", "\"max_age_days\": 900")]
    [InlineData("\"earliest\": \"19:30\"", "\"earliest\": \"25:99\"")]
    public void Invalid_tables_are_rejected(string find, string replace)
    {
        var json = Engines.TableJson();
        Assert.Contains(find, json, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => ReferenceTable.Parse(json.Replace(find, replace, StringComparison.Ordinal)));
    }

    [Fact]
    public void Garbage_json_is_a_format_error()
    {
        Assert.Throws<FormatException>(() => ReferenceTable.Parse("{ not json"));
    }
}
