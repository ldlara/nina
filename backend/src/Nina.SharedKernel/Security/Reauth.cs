using System.Security.Claims;

namespace Nina.SharedKernel.Security;

/// <summary>Escopos de reautenticação do contrato (<c>ReauthScope</c>).</summary>
public static class ReauthScopes
{
    public const string AccountPasswordChange = "ACCOUNT_PASSWORD_CHANGE";
    public const string AccountEmailChange = "ACCOUNT_EMAIL_CHANGE";
    public const string IdentityLink = "IDENTITY_LINK";
    public const string IdentityUnlink = "IDENTITY_UNLINK";
    public const string DataExportRequest = "DATA_EXPORT_REQUEST";
    public const string DataExportDownload = "DATA_EXPORT_DOWNLOAD";
    public const string AccountDelete = "ACCOUNT_DELETE";
    public const string PrivacyRequest = "PRIVACY_REQUEST";
    public const string BabyDelete = "BABY_DELETE";
    public const string OwnershipTransfer = "OWNERSHIP_TRANSFER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        AccountPasswordChange, AccountEmailChange, IdentityLink, IdentityUnlink, DataExportRequest,
        DataExportDownload, AccountDelete, PrivacyRequest, BabyDelete, OwnershipTransfer,
    };
}

/// <summary>Prova de reautenticação aceita: o <c>jti</c> consumido serve de evidência na auditoria/pedido (SR-016).</summary>
public sealed record ReauthProof(string Jti, string Scope);

/// <summary>
/// Valida e CONSOME (uso único) o <c>X-Reauth-Token</c> para uma operação sensível. Implementado pelo módulo Identity;
/// os demais módulos (Privacy, Family) dependem só desta interface. Falha: <c>401 REAUTH_REQUIRED</c>.
/// </summary>
public interface IReauthVerifier
{
    Task<ReauthProof> RequireAsync(ClaimsPrincipal user, string? reauthToken, string requiredScope, CancellationToken cancellationToken);
}
