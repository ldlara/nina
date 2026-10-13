namespace Nina.Tracking;

/// <summary>Configuração do módulo (seção <c>Tracking</c>). Limites de negócio publicados ao cliente vêm de <c>app_parameter</c> (<c>limits.*</c>).</summary>
public sealed class TrackingOptions
{
    public const string SectionName = "Tracking";

    /// <summary>Teto de pushes por minuto por usuário (SEC-040).</summary>
    public int PushPerUserPerMinute { get; set; } = 240;

    /// <summary>Teto de pushes por minuto por dispositivo (claim <c>did</c>).</summary>
    public int PushPerDevicePerMinute { get; set; } = 120;

    /// <summary>Teto de pulls por minuto por usuário.</summary>
    public int PullPerUserPerMinute { get; set; } = 600;

    /// <summary>Teto de leituras (timeline, evento, histórico, agregações) por minuto por usuário.</summary>
    public int ReadPerUserPerMinute { get; set; } = 600;

    /// <summary>Cota diária de mutações aceitas por bebê quando <c>limits.sync_max_events_per_baby_per_day</c> não está em banco.</summary>
    public int DefaultMaxEventsPerBabyPerDay { get; set; } = 20_000;

    /// <summary>Teto de entidades vivas por bebê quando <c>limits.sync_max_entities_per_baby</c> não está em banco.</summary>
    public int DefaultMaxEntitiesPerBaby { get; set; } = 250_000;
}
