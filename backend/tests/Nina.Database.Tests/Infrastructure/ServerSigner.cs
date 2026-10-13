using System.Security.Cryptography;
using System.Text;

namespace Nina.Database.Tests.Infrastructure;

/// <summary>
/// Implementação INDEPENDENTE do lado API do MAC servidor -> banco (NR-03/NR-09): os testes assinam como a API faria, para provar o formato
/// canônico documentado em <c>specs/database-spec.md</c>. Nenhuma sessão de teste como <c>nina_app</c> recebe esta chave.
/// </summary>
public static class ServerSigner
{
    public static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("nina-database-tests-server-key"));

    public static byte[] Sign(string purpose, string message, byte[]? key = null) =>
        HMACSHA256.HashData(key ?? Key, Encoding.UTF8.GetBytes(purpose + "|" + message));

    public static string Hex(byte[] value) => Convert.ToHexStringLower(value);

    public static string ReauthIssue(Guid user, Guid session, byte[] jtiHash, string[] scopes, DateTimeOffset issued, DateTimeOffset expires) =>
        $"{user:D}|{session:D}|{Hex(jtiHash)}|{string.Join(',', scopes.Order(StringComparer.Ordinal))}|{issued.ToUnixTimeSeconds()}|{expires.ToUnixTimeSeconds()}";

    public static string EmailCode(Guid user, byte[] codeHash, DateTimeOffset expires) =>
        $"{user:D}|{Hex(codeHash)}|{expires.ToUnixTimeSeconds()}";

    public static string EmailChange(Guid user, byte[] codeHash, string newEmail, DateTimeOffset expires) =>
        $"{user:D}|{Hex(codeHash)}|{newEmail.Trim().ToLowerInvariant()}|{expires.ToUnixTimeSeconds()}";

    public static string SocialRegister(Guid user, string provider, string subject, string email) =>
        $"{user:D}|{provider}|{subject}|{email.Trim().ToLowerInvariant()}";
}
