using Microsoft.Extensions.DependencyInjection;

namespace Nina.Family;

/// <summary>Ponto de entrada do módulo Family (somente estrutura; sem regras de negócio).</summary>
public static class FamilyModule
{
    public const string Name = "Family";

    public static IServiceCollection AddFamilyModule(this IServiceCollection services) => services;
}
