using System.Text.RegularExpressions;
using Nina.Identity.Contracts;
using Nina.SharedKernel.Http;

namespace Nina.Identity.Services;

/// <summary>Acumula erros de campo (códigos estáveis) e lança <c>400 VALIDATION_FAILED</c> (ADR-0010 item 9).</summary>
public sealed partial class Validation
{
    private readonly List<FieldError> _errors = [];

    public bool IsValid => _errors.Count == 0;

    public void Add(FieldError? error)
    {
        if (error is not null)
        {
            _errors.Add(error);
        }
    }

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
        {
            throw ProblemException.Validation(_errors);
        }
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public string? Email(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _errors.Add(new FieldError(field, "REQUIRED"));
            return null;
        }

        var normalized = NormalizeEmail(value);
        if (normalized.Length > 254 || !EmailRegex().IsMatch(normalized))
        {
            _errors.Add(new FieldError(field, "INVALID_FORMAT"));
            return null;
        }

        return normalized;
    }

    public string? Required(string field, string? value, int maxLength = 4096, int minLength = 1)
    {
        if (string.IsNullOrEmpty(value))
        {
            _errors.Add(new FieldError(field, "REQUIRED"));
            return null;
        }

        if (value.Length > maxLength)
        {
            _errors.Add(new FieldError(field, "TOO_LONG"));
            return null;
        }

        if (value.Length < minLength)
        {
            _errors.Add(new FieldError(field, "TOO_SHORT"));
            return null;
        }

        return value;
    }

    /// <summary>Código de verificação: 6 a 12 dígitos (contrato 1.0.1).</summary>
    public string? VerificationCode(string field, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _errors.Add(new FieldError(field, "REQUIRED"));
            return null;
        }

        if (!CodeRegex().IsMatch(value))
        {
            _errors.Add(new FieldError(field, "INVALID_FORMAT"));
            return null;
        }

        return value;
    }

    public string? Locale(string field, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!LocaleRegex().IsMatch(value) || value.Length > 35)
        {
            _errors.Add(new FieldError(field, "INVALID_FORMAT"));
            return null;
        }

        // O banco exige o idioma em minúsculas (SR-021); o contrato aceita qualquer caixa.
        var separator = value.IndexOf('-', StringComparison.Ordinal);
        return separator < 0 ? value.ToLowerInvariant() : value[..separator].ToLowerInvariant() + value[separator..];
    }

    public string? Timezone(string field, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!TimezoneRegex().IsMatch(value) || value.Length > 64)
        {
            _errors.Add(new FieldError(field, "INVALID_FORMAT"));
            return null;
        }

        return value;
    }

    public DeviceInfo? Device(string field, DeviceInfo? device)
    {
        if (device is null)
        {
            _errors.Add(new FieldError(field, "REQUIRED"));
            return null;
        }

        if (device.DeviceId is null || device.DeviceId == Guid.Empty)
        {
            _errors.Add(new FieldError(field + ".device_id", "REQUIRED"));
        }

        if (device.Platform is not ("IOS" or "ANDROID"))
        {
            _errors.Add(new FieldError(field + ".platform", device.Platform is null ? "REQUIRED" : "UNSUPPORTED_VALUE"));
        }

        if (device.DeviceLabel is { Length: > 80 })
        {
            _errors.Add(new FieldError(field + ".device_label", "TOO_LONG"));
        }

        if (device.AppVersion is { Length: > 32 })
        {
            _errors.Add(new FieldError(field + ".app_version", "TOO_LONG"));
        }

        return device;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}$")]
    private static partial Regex LocaleRegex();

    [GeneratedRegex("^[0-9]{6,12}$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+){0,2}$")]
    private static partial Regex TimezoneRegex();
}
