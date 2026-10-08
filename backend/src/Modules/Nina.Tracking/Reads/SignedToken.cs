using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nina.Tracking.Reads;

/// <summary>Token opaco de paginação (<c>page_token</c>): JSON assinado por HMAC (<c>base64url(json).base64url(mac16)</c>); adulterar invalida.</summary>
internal sealed class SignedToken(byte[] secret)
{
    public const int MaxLength = 512;

    public string Seal<T>(T payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Convert.ToBase64String(body).Replace('+', '-').Replace('/', '_').TrimEnd('=') + "." + Mac(body);
    }

    public T? Open<T>(string? token)
        where T : class
    {
        try
        {
            if (string.IsNullOrEmpty(token) || token.Length > MaxLength)
            {
                return null;
            }

            var parts = token.Split('.');
            if (parts.Length != 2)
            {
                return null;
            }

            var padded = parts[0].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var body = Convert.FromBase64String(padded);
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(body)), Encoding.ASCII.GetBytes(parts[1]))
                ? JsonSerializer.Deserialize<T>(body)
                : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private string Mac(byte[] body) =>
        Convert.ToBase64String(HMACSHA256.HashData(secret, body)[..16]).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
