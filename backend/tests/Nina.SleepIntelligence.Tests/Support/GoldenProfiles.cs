namespace Nina.SleepIntelligence.Tests.Support;

/// <summary>Descrição de um "bebê sintético" (perfis A–J da test-strategy 4.6) para os golden tests.</summary>
internal sealed record GoldenProfile(string Name, string Description, SleepPredictionRequest Request, DateTimeOffset AsOf);

internal static class GoldenProfiles
{
    private const int Bed1930 = (19 * 60) + 30;

    public static IReadOnlyList<GoldenProfile> All { get; } = Build();

    private static List<GoldenProfile> Build()
    {
        var list = new List<GoldenProfile>();

        // A: regular, 8 meses completos (270 dias), 2 sonecas, wake window real de 120 min (tabela: 165).
        {
            var b = new SyntheticBaby().Days(new DateOnly(2026, 9, 25), 14, 2, 120, 60, 150, Bed1930, 4, seed: 101);
            var asOf = TodayAfter(b, napMinutes: 55, wake: 125, naps: 1).AddMinutes(15);
            list.Add(new("A_regular_8m", "Rotina regular; 14 dias; 1ª soneca de hoje concluída.", Req.For(b.Records, 270, asOf), asOf));
        }

        // B: irregular, 6 meses (180 dias).
        {
            var b = new SyntheticBaby().Days(new DateOnly(2026, 9, 25), 14, 3, 125, 45, 120, (19 * 60) + 45, 90, seed: 202);
            var asOf = b.LastEnd.AddMinutes(25);
            list.Add(new("B_irregular_6m", "Horários caóticos (jitter ±90 min); confiança deve ser baixa.", Req.For(b.Records, 180, asOf), asOf));
        }

        // C: uma soneca, 18 meses (540 dias).
        {
            var b = new SyntheticBaby().Days(new DateOnly(2026, 9, 25), 14, 1, 270, 90, 270, (19 * 60) + 45, 5, seed: 303);
            var asOf = b.LastEnd.AddMinutes(60);
            list.Add(new("C_one_nap_18m", "Uma soneca por dia; manhã do dia seguinte.", Req.For(b.Records, 540, asOf), asOf));
        }

        // D: prematuro (150 dias cronológicos, 60 dias de prematuridade => 90 corrigidos).
        {
            var b = new SyntheticBaby().Days(new DateOnly(2026, 10, 1), 7, 4, 80, 45, 90, (20 * 60) + 30, 3, seed: 404);
            var asOf = b.LastEnd.AddMinutes(30);
            list.Add(new("D_premature_corrected_age", "Idade corrigida de 90 dias define a faixa (cronológica seria 150).", Req.For(b.Records, 150, asOf, dueOffsetDays: 60), asOf));
        }

        // E: mudança de fase: 7 dias com 3 sonecas e depois 7 dias com 2 sonecas.
        {
            var b = new SyntheticBaby()
                .Days(new DateOnly(2026, 9, 25), 7, 3, 120, 45, 150, Bed1930, 4, seed: 505)
                .Days(new DateOnly(2026, 10, 2), 7, 2, 165, 70, 180, Bed1930, 4, seed: 506);
            var asOf = b.LastEnd.AddMinutes(20);
            list.Add(new("E_phase_change_3_to_2_naps", "A rotina muda de 3 para 2 sonecas; recência pesa mais.", Req.For(b.Records, 270, asOf), asOf));
        }

        // F: histórico esparso (3 registros).
        {
            var b = new SyntheticBaby();
            var t = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 8);
            b.Add(t, t.AddMinutes(50), SleepKind.Nap);
            var asOf = t.AddMinutes(50 + 40);
            list.Add(new("F_sparse_history", "Um único registro: segue na baseline por idade.", Req.For(b.Records, 100, asOf), asOf));
        }

        // G: recém-nascido sem histórico.
        {
            var asOf = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 10);
            list.Add(new("G_newborn_cold_start", "Sem histórico, 20 dias de vida.", Req.For([], 20, asOf), asOf));
        }

        // H: histórico atravessando o horário de verão de Nova York (8/mar/2026), bebê de 9 meses.
        {
            var b = new SyntheticBaby(Tz.NewYork).Days(new DateOnly(2026, 2, 27), 14, 2, 150, 60, 150, Bed1930, 4, seed: 808);
            var asOf = b.LastEnd.AddMinutes(20);
            list.Add(new("H_dst_new_york", "Atravessa o dia de 23 h (8/mar/2026) em America/New_York.", Req.For(b.Records, 270, asOf, Tz.NewYork), asOf));
        }

        // I: a família viajou de São Paulo para Lisboa (histórico no fuso antigo).
        {
            var b = new SyntheticBaby(Tz.SaoPaulo).Days(new DateOnly(2026, 9, 25), 14, 2, 150, 60, 150, Bed1930, 4, seed: 909);
            var asOf = b.LastEnd.AddMinutes(20);
            list.Add(new("I_timezone_change_sp_to_lisbon", "Perfil do bebê agora em Europe/Lisbon; hábitos do fuso antigo não são transferidos.", Req.For(b.Records, 270, asOf, Tz.Lisbon), asOf));
        }

        // J: 60 dias de dados com preferências do cuidador (meta de 3 sonecas, bedtime 19:00-19:30).
        {
            var b = new SyntheticBaby().Days(new DateOnly(2026, 8, 10), 60, 2, 150, 60, 150, Bed1930, 6, seed: 1010);
            var asOf = b.LastEnd.AddMinutes(20);
            var prefs = new SleepPreferences(3, new TimeOnly(19, 0), new TimeOnly(19, 30));
            list.Add(new("J_long_history_with_preferences", "60 dias de dados; meta de 3 sonecas e faixa de bedtime 19:00-19:30.", Req.For(b.Records, 270, asOf, prefs: prefs), asOf));
        }

        return list;
    }

    private static DateTimeOffset TodayAfter(SyntheticBaby b, int napMinutes, int wake, int naps)
    {
        var cursor = b.LastEnd;
        for (var i = 0; i < naps; i++)
        {
            var start = cursor.AddMinutes(wake);
            var end = start.AddMinutes(napMinutes);
            b.Add(start, end, SleepKind.Nap);
            cursor = end;
        }

        return cursor;
    }
}
