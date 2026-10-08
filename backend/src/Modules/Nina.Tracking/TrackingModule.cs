using Microsoft.Extensions.DependencyInjection;

namespace Nina.Tracking;

/// <summary>Ponto de entrada do módulo Tracking (somente estrutura; sem regras de negócio).</summary>
public static class TrackingModule
{
    public const string Name = "Tracking";

    public static IServiceCollection AddTrackingModule(this IServiceCollection services) => services;
}
