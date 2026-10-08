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
        services.TryAddSingleton<SecretKeys>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SecretKeys, IOptions<JwtOptions>, TimeProvider>((o, keys, jwt, time) =>
            {
                o.MapInboundClaims = false;
                o.RequireHttpsMetadata = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Value.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Value.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = keys.SigningKey,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(jwt.Value.ClockSkewSeconds),
                    LifetimeValidator = TokenLifetime.Validator(time, TimeSpan.FromSeconds(jwt.Value.ClockSkewSeconds)),
                    NameClaimType = "sub",
                };
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
        return services;
    }

    private static async Task ValidateSessionAsync(TokenValidatedContext ctx)
    {
        var principal = ctx.Principal;
        var userId = principal.GetUserId();
        var sessionId = principal.GetSessionId();
        if (userId is null || sessionId is null)
        {
            ctx.HttpContext.Items[FailureCodeKey] = "INVALID_TOKEN";
            ctx.Fail("missing claims");
            return;
        }

        var allowRevoked = ctx.HttpContext.GetEndpoint()?.Metadata.GetMetadata<AllowRevokedSessionAttribute>() is not null;
        var validator = ctx.HttpContext.RequestServices.GetService<ISessionValidator>();
        if (validator is null || allowRevoked)
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
