using System.Security.Claims;
using Nina.Identity.Persistence;
using Nina.SharedKernel.Audit;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;

namespace Nina.Identity.Services;

/// <summary>
/// Valida e consome o <c>X-Reauth-Token</c> (SR-013.1): assinatura/<c>typ</c>/<c>aud</c>, usuário e sessão corretos, escopo da
/// operação e <b>uso único</b> do <c>jti</c> (livro-razão atômico no banco, válido entre instâncias).
/// </summary>
public sealed class ReauthService(TokenService tokens, NinaDb db, SecretKeys keys, AuditLog audit, TimeProvider time) : IReauthVerifier
{
    public async Task<ReauthProof> RequireAsync(ClaimsPrincipal user, string? reauthToken, string requiredScope, CancellationToken cancellationToken)
    {
        var userId = user.RequireUserId();
        var sessionId = user.RequireSessionId();
        var claims = await tokens.ParseReauthAsync(reauthToken, userId, sessionId);
        if (claims is null || (claims.Scopes.Count > 0 && !claims.Scopes.Contains(requiredScope)))
        {
            throw Denied();
        }

        var now = time.GetUtcNow();
        var hash = keys.Hmac("reauth-jti", claims.Jti);
        var consumed = await db.InTransactionAsync(userId, async tx =>
        {
            if (!await IdentityStore.TryConsumeOneTimeAsync(tx, userId, hash, now, claims.ExpiresAt.AddMinutes(1)))
            {
                return false;
            }

            await audit.AppendAsync(tx, new AuditEntry(
                "auth.reauth_consumed", userId, "session", sessionId,
                Metadata: new Dictionary<string, object?> { ["scope"] = requiredScope, ["jti"] = claims.Jti }));
            return true;
        }, cancellationToken);

        return consumed ? new ReauthProof(claims.Jti, requiredScope) : throw Denied();
    }

    private static ProblemException Denied() =>
        ProblemException.Unauthorized("REAUTH_REQUIRED", "Reauthentication required");
}
