using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

/// <summary>
/// Golden tests (test-strategy 4.6): perfis de bebês sintéticos com saída esperada versionada em <c>Golden/*.json</c>.
/// Mudar uma saída exige aprovação explícita de revisor (regenerar com <c>NINA_UPDATE_GOLDEN=1</c> e justificar no PR).
/// Os mesmos arquivos servem de fixture para a paridade com os clientes iOS/Android (promover a /contracts/fixtures/sleep-engine).
/// </summary>
public sealed class GoldenTests
{
    private static readonly bool UpdateMode = Environment.GetEnvironmentVariable("NINA_UPDATE_GOLDEN") == "1";
    private readonly SleepPredictionEngine _engine = Engines.Create();

    public static IEnumerable<object[]> Names() => GoldenProfiles.All.Select(p => new object[] { p.Name });

    private static string SourceDir([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Golden");

    private static string FixturePath(string name) =>
        UpdateMode ? Path.Combine(SourceDir(), name + ".json") : Path.Combine(AppContext.BaseDirectory, "Golden", name + ".json");

    private SleepPredictionResult Actual(GoldenProfile p) => _engine.Predict(p.Request, p.AsOf);

    [Theory]
    [MemberData(nameof(Names))]
    public void Engine_output_matches_the_approved_snapshot(string name)
    {
        var profile = GoldenProfiles.All.Single(p => p.Name == name);
        var actual = Actual(profile);

        if (UpdateMode)
        {
            var doc = new JsonObject
            {
                ["name"] = profile.Name,
                ["description"] = profile.Description,
                ["as_of"] = profile.AsOf.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["request"] = Json.Node(profile.Request),
                ["expected"] = Json.Node(actual),
            };
            Directory.CreateDirectory(SourceDir());
            File.WriteAllText(FixturePath(name), doc.ToJsonString(Json.Options) + "\n");
            return;
        }

        var path = FixturePath(name);
        Assert.True(File.Exists(path), $"Fixture ausente: {name}.json. Gere com NINA_UPDATE_GOLDEN=1 e peça revisão.");
        var fixture = JsonNode.Parse(File.ReadAllText(path))!;

        // 1) a entrada do arquivo (consumida também pelos clientes) é a mesma que o gerador produz
        var requestFromFile = JsonSerializer.Deserialize<SleepPredictionRequest>(fixture["request"]!.ToJsonString(), Json.Options)!;
        Assert.Equal(Json.Serialize(profile.Request), Json.Serialize(requestFromFile));

        // 2) a saída do motor é exatamente a aprovada
        var expected = fixture["expected"]!.ToJsonString();
        var actualNormalized = JsonNode.Parse(Json.Serialize(actual))!.ToJsonString();
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actualNormalized)),
            $"Saída divergente do golden '{name}'. Se a mudança é intencional, regenere com NINA_UPDATE_GOLDEN=1 e justifique no PR.\nEsperado:\n{expected}\nAtual:\n{actualNormalized}");
    }

    [Fact]
    public void Every_fixture_file_has_a_profile_and_vice_versa()
    {
        var dir = UpdateMode ? SourceDir() : Path.Combine(AppContext.BaseDirectory, "Golden");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order().ToList() : [];
        Assert.Equal(GoldenProfiles.All.Select(p => p.Name).Order(), files);
    }

    [Fact]
    public void Profiles_cover_the_scenarios_required_by_the_strategy()
    {
        var names = GoldenProfiles.All.Select(p => p.Name).ToList();
        foreach (var needle in new[] { "regular", "irregular", "one_nap", "premature", "phase_change", "sparse", "newborn", "dst", "timezone" })
        {
            Assert.Contains(names, n => n.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Golden_outputs_embody_the_expected_qualitative_behaviour()
    {
        SleepPredictionResult Of(string name) => Actual(GoldenProfiles.All.Single(p => p.Name == name));

        var regular = Of("A_regular_8m");
        Assert.Equal(ConfidenceLevel.High, regular.Nap().Confidence);
        Assert.True((int)regular.Nap().Explanation.Params["typical_wake_minutes"] < 165);

        Assert.Equal(ConfidenceLevel.Low, Of("B_irregular_6m").Nap().Confidence);
        Assert.Equal(1, Of("C_one_nap_18m").Plan!.ExpectedNapCount);
        Assert.True(Of("D_premature_corrected_age").Age!.CorrectionApplied);
        Assert.Equal(PredictionStatus.Available, Of("E_phase_change_3_to_2_naps").Status);
        Assert.True(Of("F_sparse_history").Nap().BaselineOnly);
        Assert.True(Of("G_newborn_cold_start").Nap().BaselineOnly);
        Assert.True(Of("I_timezone_change_sp_to_lisbon").Bed().BaselineOnly);

        var j = Of("J_long_history_with_preferences");
        Assert.InRange(j.Bed().PredictedStart.Local(), (19 * 60), (19 * 60) + 30);
    }
}
