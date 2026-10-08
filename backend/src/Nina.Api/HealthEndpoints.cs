namespace Nina.Api;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: o processo responde.
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

        // Readiness: módulos registrados. Verificação de PostgreSQL entra com a persistência.
        app.MapGet("/ready", () => Results.Ok(new { status = "ready", modules = ModuleRegistration.ModuleNames }));
        return app;
    }
}
