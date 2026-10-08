using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

/// <summary>RNF-014 / RB-005 / RB-013: linguagem probabilística, sem diagnóstico, julgamento, garantia ou comparação.</summary>
public sealed class MessagesTests
{
    private static readonly MessageCatalog Catalog = MessageCatalog.LoadDefault();
    private static readonly LanguageGuard Guard = new();

    [Fact]
    public void Every_emitted_key_has_a_reference_text_and_the_catalog_has_no_orphan_keys()
    {
        foreach (var key in MessageKeys.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(Catalog.Template(key)), key);
        }

        Assert.Equal(MessageKeys.All.Order(StringComparer.Ordinal), Catalog.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void All_explanation_texts_pass_the_language_guard()
    {
        foreach (var key in MessageKeys.All.Where(k => k != MessageKeys.Disclaimer))
        {
            Assert.Empty(Guard.FindViolations(Catalog.Template(key)!));
        }
    }

    [Fact]
    public void Disclaimer_states_it_is_an_estimate_not_a_diagnosis()
    {
        var text = Catalog.Template(MessageKeys.Disclaimer)!;
        Assert.Contains("orientativas", text, StringComparison.Ordinal);
        Assert.Contains("não são diagnóstico", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Seu bebê precisa dormir agora.")]
    [InlineData("A soneca está atrasada.")]
    [InlineData("Isso é normal para a idade.")]
    [InlineData("Sono anormal.")]
    [InlineData("Ele vai dormir a noite toda.")]
    [InlineData("Pode ser cólica ou refluxo.")]
    [InlineData("Seu bebê está cansado!")]
    [InlineData("Dormiu pouco hoje.")]
    [InlineData("Melhor que a maioria dos outros bebês.")]
    [InlineData("Garantimos o horário.")]
    [InlineData("Precisão: 83% (percentil 40)")]
    public void Guard_flags_forbidden_phrasing(string text) => Assert.NotEmpty(Guard.FindViolations(text));

    [Theory]
    [InlineData("Pode ser hora da soneca entre 14:10 e 14:40.")]
    [InlineData("Baseado na idade e nos últimos 7 dias de registros.")]
    [InlineData("O bebê costuma ficar acordado cerca de 135 min.")]
    [InlineData("Esta soneca durou 25 min.")]
    public void Guard_accepts_probabilistic_factual_phrasing(string text) => Assert.Empty(Guard.FindViolations(text));

    [Fact]
    public void Guard_matches_whole_words_only_and_the_list_is_configurable()
    {
        Assert.Empty(Guard.FindViolations("Informações anormalizadas")); // "anormal" só como palavra inteira
        var custom = new LanguageGuard(["banana*"]);
        Assert.NotEmpty(custom.FindViolations("bananada"));
        Assert.Empty(custom.FindViolations("precisa"));
    }

    [Fact]
    public void Render_substitutes_params_and_falls_back_to_the_key()
    {
        var p = new SortedDictionary<string, object>(StringComparer.Ordinal) { ["age_months"] = 9, ["records_used"] = 24, ["days_used"] = 7, ["typical_wake_minutes"] = 135 };
        var text = Catalog.Render(new Explanation(MessageKeys.HistoryAndAge, p));
        Assert.Contains("9 meses", text, StringComparison.Ordinal);
        Assert.Contains("7 dias", text, StringComparison.Ordinal);
        Assert.Contains("135 min", text, StringComparison.Ordinal);
        Assert.Equal("unknown.key", Catalog.Render(new Explanation("unknown.key", p)));
    }

    [Fact]
    public void Contract_enum_mapping_matches_openapi_v1_0_1()
    {
        Assert.Equal("BUILDING", ContractNames.Of(ConfidenceLevel.Low));
        Assert.Equal("FAIR", ContractNames.Of(ConfidenceLevel.Medium));
        Assert.Equal("GOOD", ContractNames.Of(ConfidenceLevel.High));
        Assert.Equal("NEXT_NAP", ContractNames.Of(PredictionKind.NextNap));
        Assert.Equal("BEDTIME", ContractNames.Of(PredictionKind.Bedtime));
        Assert.Equal("AVAILABLE", ContractNames.Of(PredictionStatus.Available));
        Assert.Equal("SLEEPING", ContractNames.Of(PredictionStatus.Sleeping));
        Assert.Equal("INSUFFICIENT_DATA", ContractNames.Of(PredictionStatus.InsufficientData));
        Assert.Equal("prediction.estimate_not_diagnosis", SleepEngineInfo.DisclaimerKey);
        Assert.StartsWith("rules-", SleepEngineInfo.ModelVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_explanation_example_keys_exist()
    {
        // O exemplo do contrato usa prediction.explain.history_and_age com estes parâmetros.
        Assert.Equal("prediction.explain.history_and_age", MessageKeys.HistoryAndAge);
        var asOf = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 14);
        var baby = new SyntheticBaby().Days(new DateOnly(2026, 9, 25), 14, 2, 150, 60, 150, (19 * 60) + 30, 4, seed: 2);
        var r = Engines.Create().Predict(Req.For(baby.Records, 270, baby.LastEnd.AddMinutes(10)), baby.LastEnd.AddMinutes(10));
        foreach (var param in new[] { "age_months", "days_used", "records_used", "typical_wake_minutes" })
        {
            Assert.True(r.Nap().Explanation.Params.ContainsKey(param), param);
        }

        _ = asOf;
    }
}
