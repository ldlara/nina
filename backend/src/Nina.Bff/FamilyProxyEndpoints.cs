namespace Nina.Bff;

/// <summary>
/// Superfície pública do módulo Family (contrato v1.0.1): bebês, cuidadores e convites. O BFF apenas expõe e encaminha;
/// autenticação, autorização por bebê/papel, limites e regras vivem na API interna. Rotas fora da lista não existem no BFF (404).
/// <c>PUT/DELETE /v1/subscriptions/family-seat</c> pertence ao módulo Subscriptions e não é exposto aqui.
/// </summary>
public static class FamilyProxyEndpoints
{
    private static readonly (string Method, string Pattern)[] Routes =
    [
        ("GET", "/v1/babies"),
        ("POST", "/v1/babies"),
        ("GET", "/v1/babies/{baby_id:guid}"),
        ("PATCH", "/v1/babies/{baby_id:guid}"),
        ("DELETE", "/v1/babies/{baby_id:guid}"),
        ("GET", "/v1/babies/{baby_id:guid}/caregivers"),
        ("PATCH", "/v1/babies/{baby_id:guid}/caregivers/{membership_id:guid}"),
        ("DELETE", "/v1/babies/{baby_id:guid}/caregivers/{membership_id:guid}"),
        ("POST", "/v1/babies/{baby_id:guid}/invitations"),
        ("POST", "/v1/babies/{baby_id:guid}/invitations/{membership_id:guid}/resend"),
        ("POST", "/v1/babies/{baby_id:guid}/ownership-transfer"),
        ("POST", "/v1/invitations/inspect"),
        ("POST", "/v1/invitations/accept"),
        ("POST", "/v1/invitations/decline"),
    ];

    public static IEndpointRouteBuilder MapFamilyProxy(this IEndpointRouteBuilder app)
    {
        foreach (var (method, pattern) in Routes)
        {
            app.MapMethods(pattern, [method], (HttpContext context, ApiForwarder forwarder) => forwarder.ForwardAsync(context));
        }

        return app;
    }
}
