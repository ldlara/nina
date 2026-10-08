using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Nina.Identity.Crypto;

/// <summary>
/// Argon2id em formato PHC (<c>$argon2id$v=19$m=..,t=..,p=..$salt$hash</c>). A verificação lê os parâmetros do próprio hash,
/// permitindo reforçar o custo depois (<c>needsRehash</c>). Concorrência limitada para não esgotar memória.
/// </summary>
public sealed class PasswordHasher : IDisposable
{
    private const string Algorithm = "ARGON2ID";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly IdentityOptions _options;
    private readonly SemaphoreSlim _gate = new(Math.Max(2, Environment.ProcessorCount));
    private readonly Lazy<string> _dummy;

    public PasswordHasher(IOptions<IdentityOptions> options)
    {
        _options = options.Value;
        _dummy = new Lazy<string>(() => HashCore("dummy-password-for-timing-equalization"));
    }

    public static string AlgorithmName => Algorithm;

    public void Dispose() => _gate.Dispose();

    public Task<string> HashAsync(string password) => RunAsync(() => HashCore(password));

    public Task<PasswordCheck> VerifyAsync(string password, string phc) => RunAsync(() => VerifyCore(password, phc));

    /// <summary>Executa um hash descartável para igualar o tempo de resposta quando a conta não existe (SEC-050).</summary>
    public Task BurnAsync(string password) => RunAsync(() => VerifyCore(password, _dummy.Value));

    private async Task<T> RunAsync<T>(Func<T> work)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string HashCore(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, _options.Argon2MemoryKiB, _options.Argon2Iterations, _options.Argon2Parallelism);
        return $"$argon2id$v=19$m={_options.Argon2MemoryKiB},t={_options.Argon2Iterations},p={_options.Argon2Parallelism}${B64(salt)}${B64(hash)}";
    }

    private PasswordCheck VerifyCore(string password, string phc)
    {
        if (password.Length == 0 || !TryParse(phc, out var m, out var t, out var p, out var salt, out var expected))
        {
            return new PasswordCheck(false, false);
        }

        var actual = Derive(password, salt, m, t, p);
        var ok = CryptographicOperations.FixedTimeEquals(actual, expected);
        var needsRehash = ok && (m != _options.Argon2MemoryKiB || t != _options.Argon2Iterations || p != _options.Argon2Parallelism);
        return new PasswordCheck(ok, needsRehash);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKiB, int iterations, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(HashBytes);
    }

    private static bool TryParse(string phc, out int m, out int t, out int p, out byte[] salt, out byte[] hash)
    {
        m = t = p = 0;
        salt = hash = [];
        var parts = phc.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=19")
        {
            return false;
        }

        try
        {
            foreach (var kv in parts[2].Split(','))
            {
                var pair = kv.Split('=');
                switch (pair[0])
                {
                    case "m": m = int.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "t": t = int.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture); break;
                    case "p": p = int.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture); break;
                    default: return false;
                }
            }

            salt = FromB64(parts[3]);
            hash = FromB64(parts[4]);
            return m is > 0 and <= 1_048_576 && t is > 0 and <= 20 && p is > 0 and <= 16 && hash.Length == HashBytes;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromB64(string value) =>
        Convert.FromBase64String(value.PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '='));
}

public readonly record struct PasswordCheck(bool Valid, bool NeedsRehash);
