using Nina.Identity.External;

namespace Nina.Identity.Tests.Infrastructure;

/// <summary>
/// Fake de id_token de Google/Apple: <c>provider|subject|email|emailVerified|nonce</c>. Nenhuma chamada de rede.
/// Qualquer outro formato, ou nonce diferente do enviado, é inválido.
/// </summary>
public sealed class FakeIdentityTokenVerifier : IIdentityTokenVerifier
{
    public static string Token(string provider, string subject, string? email, bool emailVerified, string nonce) =>
        $"{provider}|{subject}|{email}|{(emailVerified ? "true" : "false")}|{nonce}";

    public Task<VerifiedIdentity> VerifyAsync(string provider, string idToken, string nonce, CancellationToken cancellationToken)
    {
        var parts = idToken.Split('|');
        if (parts.Length != 5 || parts[0] != provider || parts[4] != nonce)
        {
            throw new IdentityTokenException("invalid");
        }

        return Task.FromResult(new VerifiedIdentity(provider, parts[1], string.IsNullOrEmpty(parts[2]) ? null : parts[2], parts[3] == "true"));
    }
}
