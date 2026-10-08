using Microsoft.Extensions.DependencyInjection;

namespace Nina.Notifications;

/// <summary>Ponto de entrada do módulo Notifications (somente estrutura; sem regras de negócio).</summary>
public static class NotificationsModule
{
    public const string Name = "Notifications";

    public static IServiceCollection AddNotificationsModule(this IServiceCollection services) => services;
}
