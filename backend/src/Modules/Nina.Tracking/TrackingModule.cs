using Microsoft.Extensions.DependencyInjection.Extensions;
using Nina.SharedKernel.Security;
using Nina.Tracking.Endpoints;
using Nina.Tracking.Reads;
using Nina.Tracking.Sync;

namespace Nina.Tracking;

/// <summary>
/// Módulo Tracking: leitura de eventos (sono, mamada, mamadeira, fralda, pumping, despertares), timeline, agregações e a sincronização
/// offline-first (<c>POST /sync/push</c> idempotente com conflito por campo; <c>GET /sync/pull</c> com cursor assinado, snapshot, delta e
/// tombstones). Fonte: ADR-0003, ADR-0009, <c>specs/sync-spike.md</c> (seção 9) e <c>contracts/openapi.yaml</c> 1.0.1.
/// </summary>
public static class TrackingModule
{
    public const string Name = "Tracking";

    public static IServiceCollection AddTrackingModule(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = services.AddOptions<TrackingOptions>();
        if (configuration is not null)
        {
            options.Bind(configuration.GetSection(TrackingOptions.SectionName));
        }

        // Chaves derivadas do segredo mestre (Security:MasterKey), uma por finalidade: o cursor de sync e o page_token não se confundem.
        services.TryAddSingleton(sp => new CursorCodec(sp.GetRequiredService<SecretKeys>().Derive("sync-cursor")));
        services.TryAddSingleton(sp => new SignedToken(sp.GetRequiredService<SecretKeys>().Derive("page-token")));
        services.TryAddScoped<PushService>();
        services.TryAddSingleton<PullService>();
        services.TryAddSingleton<ReadService>();
        services.TryAddSingleton<AggregatesService>();
        return services;
    }

    public static IEndpointRouteBuilder MapTrackingModule(this IEndpointRouteBuilder app) => app.MapTrackingEndpoints();
}
