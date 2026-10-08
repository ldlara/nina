using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Nina.SharedKernel.Security;

/// <summary>
/// Resolve a chave mestra do JWT e deriva subchaves por finalidade (HMAC de códigos, hash de IP).
/// Em Development, sem chave configurada, gera uma chave efêmera; nos demais ambientes a ausência é erro.
/// </summary>
public sealed class SecretKeys
{
    private readonly byte[] _master;

    public SecretKeys(IOptions<JwtOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.SigningKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Jwt:SigningKey não configurada (obrigatória fora de Development).");
            }

            _master = RandomNumberGenerator.GetBytes(64);
        }
        else
        {
            _master = Convert.FromBase64String(configured);
            if (_master.Length < 32)
            {
                throw new InvalidOperationException("Jwt:SigningKey deve ter ao menos 32 bytes (Base64).");
            }
        }

        SigningKey = new SymmetricSecurityKey(Derive("jwt-signing", 32)) { KeyId = "nina-1" };
    }

    public SecurityKey SigningKey { get; }

    public byte[] Derive(string purpose, int length = 32)
    {
        var okm = HMACSHA256.HashData(_master, Encoding.UTF8.GetBytes("nina.v1:" + purpose));
        return length == okm.Length ? okm : okm.AsSpan(0, Math.Min(length, okm.Length)).ToArray();
    }

    public byte[] Hmac(string purpose, string value) =>
        HMACSHA256.HashData(Derive(purpose), Encoding.UTF8.GetBytes(value));
}
