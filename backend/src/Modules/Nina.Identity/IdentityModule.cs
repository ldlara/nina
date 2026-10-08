using Microsoft.Extensions.DependencyInjection.Extensions;
using Nina.Identity.Crypto;
using Nina.Identity.Endpoints;
using Nina.Identity.External;
using Nina.Identity.Mail;
using Nina.Identity.Services;
using Nina.SharedKernel.Security;

namespace Nina.Identity;

/// <summary>Módulo Identity: cadastro, login, sessões, reautenticação, recuperação, push tokens e consentimentos.</summary>
public static class IdentityModule
{
    public const string Name = "Identity";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var options = services.AddOptions<IdentityOptions>();
        if (configuration is not null)
        {
            options.Bind(configuration.GetSection(IdentityOptions.SectionName));
        }

        services.TryAddSingleton<PasswordHasher>();
        services.TryAddSingleton<IBreachedPasswordChecker, LocalCommonPasswordChecker>();
        services.TryAddSingleton<PasswordPolicy>();

        // Fakes até haver integração real (nenhum envio/consulta externa): e-mail em memória; id_token validado localmente por JWKS.
        services.TryAddSingleton<InMemoryIdentityMailer>();
        services.TryAddSingleton<IIdentityMailer>(sp => sp.GetRequiredService<InMemoryIdentityMailer>());
        services.AddHttpClient<HttpJwksProvider>();
        services.TryAddSingleton<IJwksProvider>(sp => sp.GetRequiredService<HttpJwksProvider>());
        services.TryAddSingleton<IIdentityTokenVerifier, OidcIdentityTokenVerifier>();

        services.TryAddSingleton<TokenService>();
        services.TryAddScoped<IReauthVerifier, ReauthService>();
        services.TryAddScoped<SessionIssuer>();
        services.TryAddScoped<ConsentService>();
        services.TryAddScoped<AuthService>();
        services.TryAddScoped<AccountService>();
        services.TryAddSingleton<ISessionValidator, IdentitySessionValidator>();
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityModule(this IEndpointRouteBuilder app) => app.MapIdentityEndpoints();
}
