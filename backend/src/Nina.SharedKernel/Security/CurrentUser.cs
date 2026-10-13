using System.Security.Claims;

namespace Nina.SharedKernel.Security;

public static class ClaimsExtensions
{
    public const string SessionClaim = "sid";
    public const string DeviceClaim = "did";

    public static Guid? GetUserId(this ClaimsPrincipal? principal) => ParseGuid(principal?.FindFirst("sub")?.Value);

    public static Guid? GetSessionId(this ClaimsPrincipal? principal) => ParseGuid(principal?.FindFirst(SessionClaim)?.Value);

    public static Guid? GetDeviceId(this ClaimsPrincipal? principal) => ParseGuid(principal?.FindFirst(DeviceClaim)?.Value);

    public static Guid RequireUserId(this ClaimsPrincipal principal) =>
        principal.GetUserId() ?? throw Http.ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");

    public static Guid RequireSessionId(this ClaimsPrincipal principal) =>
        principal.GetSessionId() ?? throw Http.ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var g) ? g : null;
}

/// <summary>Verifica se a sessão do token continua ativa (revogação imediata, AZ-17). Implementado pelo módulo Identity.</summary>
public interface ISessionValidator
{
    Task<bool> IsActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>Marca endpoints que aceitam token de sessão já revogada (ex.: logout idempotente).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowRevokedSessionAttribute : Attribute;
