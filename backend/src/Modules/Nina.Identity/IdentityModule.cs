using Microsoft.Extensions.DependencyInjection;

namespace Nina.Identity;

/// <summary>Ponto de entrada do módulo Identity (somente estrutura; sem regras de negócio).</summary>
public static class IdentityModule
{
    public const string Name = "Identity";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services) => services;
}
