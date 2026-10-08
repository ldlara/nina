using Microsoft.Extensions.DependencyInjection;

namespace Nina.Privacy;

/// <summary>Ponto de entrada do módulo Privacy (somente estrutura; sem regras de negócio).</summary>
public static class PrivacyModule
{
    public const string Name = "Privacy";

    public static IServiceCollection AddPrivacyModule(this IServiceCollection services) => services;
}
