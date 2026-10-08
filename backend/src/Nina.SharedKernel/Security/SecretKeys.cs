using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Nina.SharedKernel.Security;

/// <summary>
/// Subchaves HMAC por finalidade derivadas do segredo mestre (<c>Security:MasterKey</c>). Em Development, sem chave, gera uma
/// efêmera; nos demais ambientes a ausência é erro (falha fechada).
/// </summary>
public sealed class SecretKeys
{
    private readonly byte[] _master;

    public SecretKeys(IOptions<SecurityOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.MasterKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Security:MasterKey não configurada (obrigatória fora de Development).");
            }

            _master = RandomNumberGenerator.GetBytes(64);
        }
        else
        {
            _master = Convert.FromBase64String(configured);
            if (_master.Length < 32)
            {
                throw new InvalidOperationException("Security:MasterKey deve ter ao menos 32 bytes (Base64).");
            }
        }
    }

    public byte[] Derive(string purpose, int length = 32)
    {
        var okm = HMACSHA256.HashData(_master, Encoding.UTF8.GetBytes("nina.v1:" + purpose));
        return length == okm.Length ? okm : okm.AsSpan(0, Math.Min(length, okm.Length)).ToArray();
    }

    public byte[] Hmac(string purpose, string value) =>
        HMACSHA256.HashData(Derive(purpose), Encoding.UTF8.GetBytes(value));
}

/// <summary>
/// Chaveiro de assinatura do JWT: ES256 (ECDSA P-256) com <c>kid</c>; a chave anterior pode ficar só para verificação (rotação).
/// </summary>
public sealed class JwtKeyring : IDisposable
{
    private readonly ECDsa _signing;
    private readonly List<ECDsa> _owned = [];

    public JwtKeyring(IOptions<JwtOptions> options, IHostEnvironment environment)
    {
        var o = options.Value;
        _signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (string.IsNullOrWhiteSpace(o.SigningKeyPem))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Jwt:SigningKeyPem não configurada (obrigatória fora de Development).");
            }
        }
        else
        {
            _signing.ImportFromPem(o.SigningKeyPem);
            if (_signing.KeySize != 256)
            {
                throw new InvalidOperationException("Jwt:SigningKeyPem deve ser uma chave ECDSA P-256 (ES256).");
            }
        }

        var kid = string.IsNullOrWhiteSpace(o.KeyId) ? Fingerprint(_signing) : o.KeyId;
        SigningKey = new ECDsaSecurityKey(_signing) { KeyId = kid };

        var verification = new List<SecurityKey> { new ECDsaSecurityKey(_signing) { KeyId = kid } };
        foreach (var (extraKid, pem) in o.VerificationKeys)
        {
            var extra = ECDsa.Create();
            extra.ImportFromPem(pem);
            if (extra.KeySize != 256)
            {
                throw new InvalidOperationException($"Jwt:VerificationKeys:{extraKid} deve ser ECDSA P-256.");
            }

            _owned.Add(extra);
            verification.Add(new ECDsaSecurityKey(extra) { KeyId = extraKid });
        }

        VerificationKeys = verification;
    }

    public ECDsaSecurityKey SigningKey { get; }

    public IReadOnlyList<SecurityKey> VerificationKeys { get; }

    public void Dispose()
    {
        _signing.Dispose();
        foreach (var key in _owned)
        {
            key.Dispose();
        }
    }

    private static string Fingerprint(ECDsa key) =>
        Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
}
