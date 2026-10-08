using System.Security.Cryptography;
using System.Text;

namespace Nina.Identity.Crypto;

/// <summary>Geração e hash de segredos opacos (refresh token, token de recuperação, código de verificação).</summary>
public static class OpaqueTokens
{
    private const string RefreshPrefix = "rt_";

    /// <summary>
    /// Refresh token: <c>rt_</c> + base64url(userId[16] | sessionId[16] | aleatório[32]). Os ids permitem definir o contexto
    /// de RLS antes de consultar a sessão; a segurança vem dos 256 bits aleatórios (o banco guarda só o SHA-256).
    /// </summary>
    public static string NewRefreshToken(Guid userId, Guid sessionId)
    {
        var buffer = new byte[64];
        userId.TryWriteBytes(buffer.AsSpan(0, 16));
        sessionId.TryWriteBytes(buffer.AsSpan(16, 16));
        RandomNumberGenerator.Fill(buffer.AsSpan(32));
        return RefreshPrefix + Base64Url(buffer);
    }

    public static bool TryParseRefreshToken(string? token, out Guid userId, out Guid sessionId)
    {
        userId = sessionId = Guid.Empty;
        if (token is null || !token.StartsWith(RefreshPrefix, StringComparison.Ordinal) || token.Length != RefreshPrefix.Length + 86)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = FromBase64Url(token[RefreshPrefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length != 64)
        {
            return false;
        }

        userId = new Guid(bytes.AsSpan(0, 16));
        sessionId = new Guid(bytes.AsSpan(16, 16));
        return userId != Guid.Empty && sessionId != Guid.Empty;
    }

    public static byte[] HashRefreshToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Token de uso único de 256 bits para links de recuperação.</summary>
    public static string NewUrlToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Código numérico de 6 dígitos (verificação de e-mail).</summary>
    public static string NewVerificationCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}
