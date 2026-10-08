using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nina.SharedKernel.Http;

namespace Nina.SharedKernel.Security;

public static class NinaAuthentication
{
    private const string FailureCodeKey = "nina.auth_failure_code";

    public static IServiceCollection AddNinaJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName));
        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.SectionName));
        services.TryAddSingleton<SecretKeys>();
        services.TryAddSingleton<JwtKeyring>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtKeyring, IOptions<JwtOptions>, TimeProvider>((o, keyring, jwt, time) =>
            {
                o.MapInboundClaims = false;
                o.RequireHttpsMetadata = false;
                o.TokenValidationParameters = TokenValidation.Create(jwt.Value, keyring, time, jwt.Value.Audience, TokenValidation.AccessTokenType);
                o.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = ctx =>
                    {
                        ctx.HttpContext.Items[FailureCodeKey] = ctx.Exception is SecurityTokenExpiredException
                            ? "TOKEN_EXPIRED"
                            : "INVALID_TOKEN";
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = ValidateSessionAsync,
                    OnChallenge = ChallengeAsync,
                };
            });

        services.AddAuthorization();
        services.AddHostedService<SessionValidatorStartupCheck>();
        return services;
    }

    private static async Task ValidateSessionAsync(TokenValidatedContext ctx)
    {
        var principal = ctx.Principal;
        var userId = principal.GetUserId();
        var sessionId = principal.GetSessionId();
        // Perfil AD-31: jti obrigatório e exp - iat <= 900 s (um token "válido" de vida longa é recusado).
        var jwtToken = ctx.SecurityToken as Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;
        if (userId is null || sessionId is null || jwtToken is null || string.IsNullOrEmpty(jwtToken.Id)
            || jwtToken.ValidTo - jwtToken.IssuedAt > TimeSpan.FromMinutes(15))
        {
            ctx.HttpContext.Items[FailureCodeKey] = "INVALID_TOKEN";
            ctx.Fail("missing claims");
            return;
        }

        var allowRevoked = ctx.HttpContext.GetEndpoint()?.Metadata.GetMetadata<AllowRevokedSessionAttribute>() is not null;
        // NR-17: falha FECHADO. Sem ISessionValidator a revogação de sessão não valeria; recusa o token (e a subida
        // da aplicação é barrada por SessionValidatorStartupCheck) em vez de aceitar silenciosamente.
        var validator = ctx.HttpContext.RequestServices.GetService<ISessionValidator>();
        if (validator is null)
        {
            ctx.HttpContext.Items[FailureCodeKey] = "INVALID_TOKEN";
            ctx.Fail("no session validator registered");
            return;
        }

        if (allowRevoked)
        {
            return;
        }

        if (!await validator.IsActiveAsync(userId.Value, sessionId.Value, ctx.HttpContext.RequestAborted))
        {
            ctx.HttpContext.Items[FailureCodeKey] = "SESSION_REVOKED";
            ctx.Fail("session revoked");
        }
    }

    private static async Task ChallengeAsync(JwtBearerChallengeContext ctx)
    {
        ctx.HandleResponse();
        var code = ctx.HttpContext.Items[FailureCodeKey] as string
                   ?? (ctx.AuthenticateFailure is null ? "AUTHENTICATION_REQUIRED" : "INVALID_TOKEN");
        var title = code switch
        {
            "TOKEN_EXPIRED" => "Access token expired",
            "SESSION_REVOKED" => "Session revoked",
            "INVALID_TOKEN" => "Invalid token",
            _ => "Authentication required",
        };
        ctx.Response.Headers.WWWAuthenticate = code == "AUTHENTICATION_REQUIRED"
            ? "Bearer"
            : $"Bearer error=\"invalid_token\"";
        await ProblemWriter.WriteAsync(ctx.HttpContext, ProblemException.Unauthorized(code, title));
    }
}

/// <summary>NR-17: impede a subida de um host que registrou a autenticação JWT mas esqueceu o <see cref="ISessionValidator"/>.</summary>
internal sealed class SessionValidatorStartupCheck(IServiceProvider services) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        services.GetService<ISessionValidator>() is null
            ? throw new InvalidOperationException("ISessionValidator não registrado: a revogação de sessão não seria aplicada (NR-17).")
            : Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
