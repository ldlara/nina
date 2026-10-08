namespace Nina.Bff;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public string BaseUrl { get; set; } = "http://localhost:8080";

    public int TimeoutSeconds { get; set; } = 10;
}
