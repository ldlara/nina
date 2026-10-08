using Microsoft.Extensions.DependencyInjection;

namespace Nina.SleepIntelligence;

/// <summary>Ponto de entrada do módulo SleepIntelligence (somente estrutura; sem regras de negócio).</summary>
public static class SleepIntelligenceModule
{
    public const string Name = "SleepIntelligence";

    public static IServiceCollection AddSleepIntelligenceModule(this IServiceCollection services) => services;
}
