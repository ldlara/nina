using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Nina.SharedKernel.Http;

/// <summary>Cabeçalhos internos BFF -> API (NR-02). Nunca fazem parte do contrato público.</summary>
public static class InternalHeaders
{
    /// <summary>Segredo compartilhado entre BFF e API; prova que o IP encaminhado foi resolvido pelo BFF.</summary>
    public const string Secret = "X-Nina-Internal-Secret";

    /// <summary>IP do cliente já resolvido pelo BFF (cabeçalhos encaminhados do roteador, rede confiável).</summary>
    public const string ClientIp = "X-Nina-Client-Ip";
}

/// <summary>Configuração <c>Internal:*</c>. Fora de Development o segredo é obrigatório (a API não sobe sem ele).</summary>
public sealed class InternalOptions
{
    public const string SectionName = "Internal";

    public const int MinSecretLength = 32;

    public string? SharedSecret { get; set; }
}

internal sealed class InternalOptionsValidator(IHostEnvironment environment) : IValidateOptions<InternalOptions>
{
    public ValidateOptionsResult Validate(string? name, InternalOptions options)
    {
        if (environment.IsDevelopment() && string.IsNullOrEmpty(options.SharedSecret))
        {
            return ValidateOptionsResult.Success; // em Development sem segredo o IP encaminhado simplesmente não é confiado
        }

        return options.SharedSecret is { Length: >= InternalOptions.MinSecretLength }
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Internal:SharedSecret é obrigatório (mínimo {InternalOptions.MinSecretLength} caracteres) fora de Development.");
    }
}

/// <summary>
/// Define <c>Connection.RemoteIpAddress</c> a partir de <see cref="InternalHeaders.ClientIp"/> somente quando
/// <see cref="InternalHeaders.Secret"/> confere (tempo constante) com <c>Internal:SharedSecret</c>. Qualquer outro
/// <c>X-Forwarded-For</c>/cabeçalho interno recebido é descartado: a API nunca confia em cabeçalhos de IP não autenticados.
/// </summary>
public sealed class InternalClientIpMiddleware(RequestDelegate next, IOptions<InternalOptions> options)
{
    private readonly byte[]? _expected = string.IsNullOrEmpty(options.Value.SharedSecret)
        ? null
        : SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.SharedSecret));

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Request.Headers;
        var trusted = false;
        if (_expected is not null && headers.TryGetValue(InternalHeaders.Secret, out var provided) && provided.Count == 1)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(provided[0] ?? string.Empty));
            trusted = CryptographicOperations.FixedTimeEquals(hash, _expected);
        }

        if (trusted
            && headers.TryGetValue(InternalHeaders.ClientIp, out var ipValue)
            && ipValue.Count == 1
            && IPAddress.TryParse(ipValue[0], out var ip))
        {
            context.Connection.RemoteIpAddress = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        }

        headers.Remove(InternalHeaders.Secret);
        headers.Remove(InternalHeaders.ClientIp);
        headers.Remove("X-Forwarded-For");
        return next(context);
    }
}
