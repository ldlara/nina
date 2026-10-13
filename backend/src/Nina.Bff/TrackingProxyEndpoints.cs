namespace Nina.Bff;

/// <summary>
/// Superfície pública do módulo Tracking (contrato 1.0.1): sincronização (<c>/sync/push</c>, <c>/sync/pull</c>), timeline, evento,
/// histórico e agregações. O BFF apenas expõe e encaminha (corpo de até 256 KiB, <c>Authorization</c>, query e cabeçalhos do contrato);
/// autenticação, papéis, cotas e regras vivem na API interna. Rotas fora desta lista não existem no BFF (404).
/// </summary>
public static class TrackingProxyEndpoints
{
    private static readonly (string Method, string Pattern)[] Routes =
    [
        ("POST", "/v1/sync/push"),
        ("GET", "/v1/sync/pull"),
        ("GET", "/v1/babies/{baby_id:guid}/timeline"),
        ("GET", "/v1/babies/{baby_id:guid}/events/{event_id:guid}"),
        ("GET", "/v1/babies/{baby_id:guid}/events/{event_id:guid}/history"),
        ("GET", "/v1/babies/{baby_id:guid}/aggregates"),
    ];

    public static IEndpointRouteBuilder MapTrackingProxy(this IEndpointRouteBuilder app)
    {
        foreach (var (method, pattern) in Routes)
        {
            app.MapMethods(pattern, [method], (HttpContext context, ApiForwarder forwarder) => forwarder.ForwardAsync(context));
        }

        return app;
    }
}
