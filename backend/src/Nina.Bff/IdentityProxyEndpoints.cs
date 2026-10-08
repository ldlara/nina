namespace Nina.Bff;

/// <summary>
/// Superfície pública do módulo Identity (contrato v1.0.0). O BFF apenas expõe e encaminha; autenticação, limites e regras
/// vivem na API interna. Rotas fora desta lista não existem no BFF (404).
/// </summary>
public static class IdentityProxyEndpoints
{
    private static readonly (string Method, string Pattern)[] Routes =
    [
        ("POST", "/v1/auth/register"),
        ("POST", "/v1/auth/email/verify"),
        ("POST", "/v1/auth/login"),
        ("POST", "/v1/auth/google"),
        ("POST", "/v1/auth/apple"),
        ("POST", "/v1/auth/refresh"),
        ("POST", "/v1/auth/logout"),
        ("POST", "/v1/auth/password/forgot"),
        ("POST", "/v1/auth/password/reset"),
        ("POST", "/v1/auth/reauthenticate"),
        ("GET", "/v1/me"),
        ("PATCH", "/v1/me"),
        ("PUT", "/v1/me/password"),
        ("POST", "/v1/me/identities"),
        ("GET", "/v1/me/sessions"),
        ("DELETE", "/v1/me/sessions/{session_id:guid}"),
        ("POST", "/v1/me/session-revocations"),
        ("PUT", "/v1/me/push-tokens/{device_id:guid}"),
        ("DELETE", "/v1/me/push-tokens/{device_id:guid}"),
        ("GET", "/v1/me/consents"),
        ("POST", "/v1/me/consents"),
        ("GET", "/v1/legal/documents"),
    ];

    public static IEndpointRouteBuilder MapIdentityProxy(this IEndpointRouteBuilder app)
    {
        foreach (var (method, pattern) in Routes)
        {
            app.MapMethods(pattern, [method], (HttpContext context, ApiForwarder forwarder) => forwarder.ForwardAsync(context));
        }

        return app;
    }
}
