using Microsoft.Extensions.Options;
using Nina.SharedKernel.Http;

namespace Nina.Identity.Crypto;

/// <summary>Consulta de senhas vazadas (SEC-010). A implementação padrão é local; uma HIBP k-anonymity pode substituí-la.</summary>
public interface IBreachedPasswordChecker
{
    Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken);
}

/// <summary>Lista local de senhas notoriamente comuns (sem rede). Não substitui a consulta k-anonymity em produção.</summary>
public sealed class LocalCommonPasswordChecker : IBreachedPasswordChecker
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "passw0rd", "123456789", "1234567890", "12345678910", "qwertyuiop",
        "qwerty123", "iloveyou12", "1q2w3e4r5t", "abc1234567", "senha12345", "senha123456", "mudar12345", "brasil12345",
        "administrador", "letmein1234", "welcome1234", "football123", "baseball123", "00000000000", "11111111111",
        "123123123123", "qazwsxedc123", "trustno1234", "changeme123", "correct-horse-battery",
    };

    public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken) =>
        Task.FromResult(Common.Contains(password) || password.Distinct().Count() == 1);
}

public sealed class PasswordPolicy(IOptions<IdentityOptions> options, IBreachedPasswordChecker breached)
{
    /// <summary>Valida a senha e devolve erros de campo (códigos estáveis para o cliente localizar).</summary>
    public async Task<FieldError?> ValidateAsync(string field, string password, string? email, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (password.Length < o.PasswordMinLength)
        {
            return Policy(field, "TOO_SHORT", o.PasswordMinLength);
        }

        if (password.Length > o.PasswordMaxLength)
        {
            return Policy(field, "TOO_LONG", o.PasswordMaxLength);
        }

        if (email is not null)
        {
            var local = email.Split('@')[0];
            if (string.Equals(password, email, StringComparison.OrdinalIgnoreCase)
                || (local.Length >= 4 && string.Equals(password, local, StringComparison.OrdinalIgnoreCase)))
            {
                return Policy(field, "TOO_COMMON", null);
            }
        }

        return await breached.IsBreachedAsync(password, cancellationToken)
            ? Policy(field, "TOO_COMMON", null)
            : null;
    }

    private static FieldError Policy(string field, string reason, int? limit)
    {
        var meta = new Dictionary<string, object?> { ["reason"] = reason };
        if (limit is { } value)
        {
            meta["limit"] = value;
        }

        return new FieldError(field, "PASSWORD_POLICY", meta);
    }
}
