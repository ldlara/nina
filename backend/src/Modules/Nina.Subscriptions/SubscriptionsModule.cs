using Microsoft.Extensions.DependencyInjection;

namespace Nina.Subscriptions;

/// <summary>Ponto de entrada do módulo Subscriptions (somente estrutura; sem regras de negócio).</summary>
public static class SubscriptionsModule
{
    public const string Name = "Subscriptions";

    public static IServiceCollection AddSubscriptionsModule(this IServiceCollection services) => services;
}
