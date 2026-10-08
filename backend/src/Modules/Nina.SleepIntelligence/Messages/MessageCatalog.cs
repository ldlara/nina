using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nina.SleepIntelligence;

/// <summary>Textos de referência pt-BR das chaves (uso no servidor e no verificador de linguagem).</summary>
public sealed partial class MessageCatalog
{
    private readonly IReadOnlyDictionary<string, string> _messages;

    private MessageCatalog(IReadOnlyDictionary<string, string> messages) => _messages = messages;

    public IReadOnlyCollection<string> Keys => _messages.Keys.ToList();

    public static MessageCatalog LoadDefault()
    {
        using var stream = typeof(MessageCatalog).Assembly.GetManifestResourceStream("Nina.SleepIntelligence.Data.messages.pt-BR.json")
            ?? throw new InvalidOperationException("Recurso de mensagens não encontrado.");
        using var doc = JsonDocument.Parse(stream);
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.GetProperty("messages").EnumerateObject())
        {
            map[p.Name] = p.Value.GetString() ?? string.Empty;
        }

        return new MessageCatalog(map);
    }

    public string? Template(string key) => _messages.TryGetValue(key, out var t) ? t : null;

    /// <summary>Substitui <c>{param}</c> pelos valores (cultura invariante). Chave desconhecida devolve a própria chave.</summary>
    public string Render(Explanation explanation)
    {
        ArgumentNullException.ThrowIfNull(explanation);
        var template = Template(explanation.Key);
        if (template is null)
        {
            return explanation.Key;
        }

        return ParamRegex().Replace(template, m =>
            explanation.Params.TryGetValue(m.Groups[1].Value, out var v)
                ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty
                : m.Value);
    }

    [GeneratedRegex(@"\{([a-z_]+)\}")]
    private static partial Regex ParamRegex();
}

/// <summary>
/// Verificador de linguagem (RNF-014, UX 11.2): detecta termos diagnósticos, imperativos, julgamentos ou garantias.
/// Termos terminados em <c>*</c> casam por prefixo. A lista é configurável.
/// </summary>
public sealed class LanguageGuard
{
    private readonly Regex[] _patterns;

    public LanguageGuard(IEnumerable<string>? forbiddenTerms = null)
    {
        _patterns = (forbiddenTerms ?? DefaultTerms)
            .Select(t => new Regex(
                @"(?<!\p{L})" + Regex.Escape(t.TrimEnd('*')) + (t.EndsWith('*') ? @"\p{L}*" : @"(?!\p{L})"),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)))
            .ToArray();
        Terms = (forbiddenTerms ?? DefaultTerms).ToList();
    }

    public static IReadOnlyList<string> DefaultTerms { get; } =
    [
        "vai dormir", "vai acordar", "deve dormir", "deveria", "precisa*", "tem que", "atrasad*", "anormal", "normal",
        "saudável", "doente", "problema*", "garant*", "com certeza", "certamente", "diagnóstic*", "sintoma*",
        "cólica*", "refluxo", "ruim", "pouco", "agitad*", "cansad*", "falhou", "percentil", "outros bebês", "outras crianças",
        "melhor que", "pior que", "dorme a noite toda",
    ];

    public IReadOnlyList<string> Terms { get; }

    /// <summary>Termos proibidos encontrados no texto (vazio = passa).</summary>
    public IReadOnlyList<string> FindViolations(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<string>();
        for (var i = 0; i < _patterns.Length; i++)
        {
            if (_patterns[i].IsMatch(text))
            {
                found.Add(Terms[i]);
            }
        }

        return found;
    }
}
