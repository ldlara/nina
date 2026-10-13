using System.Net;
using Nina.SharedKernel.Security;

namespace Nina.SharedKernel.Http;

/// <summary>Dados da requisição corrente úteis a serviços (correlação, IP, usuário autenticado).</summary>
public interface IRequestContext
{
    string RequestId { get; }

    IPAddress? ClientIp { get; }

    Guid? UserId { get; }

    Guid? SessionId { get; }
}

public sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public const string RequestIdItemKey = "nina.request_id";

    public string RequestId =>
        accessor.HttpContext?.Items[RequestIdItemKey] as string ?? Guid.NewGuid().ToString("N");

    public IPAddress? ClientIp => accessor.HttpContext?.Connection.RemoteIpAddress;

    public Guid? UserId => accessor.HttpContext?.User.GetUserId();

    public Guid? SessionId => accessor.HttpContext?.User.GetSessionId();
}
