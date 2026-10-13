using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nina.SleepIntelligence;

/// <summary>Ponto de entrada do módulo SleepIntelligence: registra o motor de previsão (biblioteca pura, sem endpoints).</summary>
public static class SleepIntelligenceModule
{
    public const string Name = "SleepIntelligence";

    public static IServiceCollection AddSleepIntelligenceModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(SleepEngineOptions.Default);
        services.TryAddSingleton(_ => ReferenceTable.LoadDefault());
        services.TryAddSingleton<ISleepPredictionEngine>(sp => new SleepPredictionEngine(
            sp.GetRequiredService<ReferenceTable>(),
            sp.GetRequiredService<SleepEngineOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
