using Microsoft.Extensions.DependencyInjection.Extensions;
using Nina.Family.Endpoints;
using Nina.Family.Mail;
using Nina.Family.Services;

namespace Nina.Family;

/// <summary>Módulo Family: bebês (perfil e idade derivada), cuidadores, convites, papéis e transferência de propriedade.</summary>
public static class FamilyModule
{
    public const string Name = "Family";

    public static IServiceCollection AddFamilyModule(this IServiceCollection services)
    {
        services.AddOptions<FamilyOptions>().BindConfiguration(FamilyOptions.SectionName);

        // E-mail de convite: fake em memória até haver integração real (nenhum envio externo).
        services.TryAddSingleton<InMemoryFamilyMailer>();
        services.TryAddSingleton<IFamilyMailer>(sp => sp.GetRequiredService<InMemoryFamilyMailer>());

        services.TryAddSingleton<FamilyDb>();
        services.TryAddSingleton<IdempotencyStore>();
        services.TryAddScoped<FamilyRateGate>();
        services.TryAddScoped<BabyService>();
        services.TryAddScoped<CaregiverService>();
        services.TryAddScoped<InvitationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapFamilyModule(this IEndpointRouteBuilder app) => app.MapFamilyEndpoints();
}
