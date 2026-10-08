namespace Nina.SleepIntelligence;

/// <summary>Estatística inteira e determinística (sem ponto flutuante), para paridade entre servidor e clientes.</summary>
internal static class Statistics
{
    // 1000 × 2^(-r/4): meia-vida de 4 dias com aritmética inteira.
    private static readonly int[] DecayQuarter = [1000, 841, 707, 595];

    /// <summary>Peso por idade do dado em dias (meia-vida 4 dias): 1000, 841, 707, 595, 500, ...</summary>
    public static int RecencyWeight(int ageDays)
    {
        var d = Math.Max(0, ageDays);
        var shift = d / 4;
        var w = shift >= 20 ? 0 : DecayQuarter[d % 4] >> shift;
        return Math.Max(1, w);
    }

    /// <summary>Mediana ponderada. Em empate exato entre dois valores, média arredondada para cima.</summary>
    public static int WeightedMedian(IReadOnlyList<(int Value, int Weight)> items)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("Sem itens.", nameof(items));
        }

        var sorted = items.OrderBy(i => i.Value).ThenBy(i => i.Weight).ToList();
        long total = sorted.Sum(i => (long)i.Weight);
        long cum = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            cum += sorted[i].Weight;
            if (cum * 2 > total)
            {
                return sorted[i].Value;
            }

            if (cum * 2 == total)
            {
                return i + 1 < sorted.Count ? (sorted[i].Value + sorted[i + 1].Value + 1) / 2 : sorted[i].Value;
            }
        }

        return sorted[^1].Value;
    }

    /// <summary>Desvio absoluto mediano ponderado em torno de <paramref name="median"/>.</summary>
    public static int WeightedMad(IReadOnlyList<(int Value, int Weight)> items, int median) =>
        items.Count == 0
            ? 0
            : WeightedMedian(items.Select(i => (Math.Abs(i.Value - median), i.Weight)).ToList());

    /// <summary>
    /// Desvio absoluto médio (sem pesos) em torno de <paramref name="median"/>, arredondado. Preferido ao MAD porque este
    /// zera quando metade dos valores coincide com a mediana, escondendo séries bimodais (irregulares).
    /// </summary>
    public static int MeanAbsoluteDeviation(IReadOnlyList<int> values, int median) =>
        values.Count == 0 ? 0 : (int)(((values.Sum(v => (long)Math.Abs(v - median)) * 2) + values.Count) / (2L * values.Count));

    public static int Median(IReadOnlyList<int> values) =>
        WeightedMedian(values.Select(v => (v, 1)).ToList());
}
