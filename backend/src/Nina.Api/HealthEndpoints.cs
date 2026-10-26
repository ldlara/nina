namespace Nina.Api;

internal static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: o processo responde.
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

        // Readiness interno (probe da plataforma; fora do contrato e da rota pública, SR-020): não lista módulos nem
        // dependências. Verifica o PostgreSQL quando há conexão configurada.
        app.MapGet("/ready", async (IConfiguration configuration, IServiceProvider services, CancellationToken ct) =>
        {
            if (configuration.GetConnectionString("Default") is null)
            {
                return Results.Ok(new { status = "ready" });
            }

            var db = services.GetRequiredService<Nina.SharedKernel.Data.NinaDb>();
            return await db.PingAsync(ct)
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        return app;
    }
}
