namespace Nina.Bff;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public string BaseUrl { get; set; } = "http://localhost:8080";

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Segredo compartilhado BFF -> API (<c>Internal:SharedSecret</c>); obrigatório fora de Development.</summary>
public sealed class InternalOptions
{
    public const string SectionName = "Internal";

    public const int MinSecretLength = 32;

    public string? SharedSecret { get; set; }
}
