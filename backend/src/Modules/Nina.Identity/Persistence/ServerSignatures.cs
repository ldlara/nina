using System.Globalization;

namespace Nina.Identity.Persistence;

/// <summary>
/// Mensagens canônicas e propósitos dos MACs servidor -> banco (NR-03/NR-09). O formato é o contrato com as funções
/// <c>nina.reauth_issue</c>, <c>nina.email_code_issue</c> e <c>nina.register_social_user</c> (ver <c>specs/database-spec.md</c>):
/// UUIDs em minúsculas com hífens, hashes em hexadecimal minúsculo, instantes em segundos Unix (truncados) e e-mail normalizado.
/// </summary>
public static class ServerSignatures
{
    public const string ReauthIssue = "reauth.issue";
    public const string EmailCode = "email.code";
    public const string SocialRegister = "social.register";

    /// <summary>Trunca para segundos inteiros: o MAC cobre o instante em segundos e o banco recebe exatamente o mesmo instante.</summary>
    public static DateTimeOffset Floor(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    public static string ReauthIssueMessage(
        Guid user, Guid session, byte[] jtiHash, IReadOnlyCollection<string> scopes, DateTimeOffset issued, DateTimeOffset expires) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{user:D}|{session:D}|{Convert.ToHexStringLower(jtiHash)}|{string.Join(',', scopes.Order(StringComparer.Ordinal))}|{issued.ToUnixTimeSeconds()}|{expires.ToUnixTimeSeconds()}");

    public static string EmailCodeMessage(Guid user, byte[] codeHash, DateTimeOffset expires) =>
        string.Create(CultureInfo.InvariantCulture, $"{user:D}|{Convert.ToHexStringLower(codeHash)}|{expires.ToUnixTimeSeconds()}");

    public static string SocialRegisterMessage(Guid user, string provider, string subject, string normalizedEmail) =>
        string.Create(CultureInfo.InvariantCulture, $"{user:D}|{provider}|{subject}|{normalizedEmail}");
}
