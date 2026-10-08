using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nina.SharedKernel.Audit;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;
using Npgsql;

namespace Nina.SharedKernel;

public static class SharedKernelExtensions
{
    /// <summary>Registra infraestrutura compartilhada: banco (papel <c>nina_app</c>), JWT, RFC 7807, rate limit, auditoria.</summary>
    public static IServiceCollection AddNinaSharedKernel(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));

        // Conexão criada sob demanda (ConnectionStrings:Default = login membro de nina_app, nunca superusuário/dono).
        services.TryAddSingleton(sp =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
                     ?? throw new InvalidOperationException("ConnectionStrings:Default não configurada.");
            return new NpgsqlDataSourceBuilder(cs).Build();
        });
        services.TryAddSingleton<NinaDb>();

        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
            o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
        });

        services.AddNinaJwtAuthentication(configuration);
        services.TryAddSingleton<IRateLimiter, InMemoryRateLimiter>();
        services.TryAddSingleton<BackgroundWorkRunner>();
        services.TryAddSingleton<IBackgroundWork>(sp => sp.GetRequiredService<BackgroundWorkRunner>());
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundWorkRunner>());
        services.TryAddScoped<IRequestContext, HttpRequestContext>();
        services.TryAddScoped<AuditLog>();

        // NR-02: a API não interpreta X-Forwarded-For (qualquer um que alcance a porta o forjaria). O IP do cliente chega
        // do BFF em cabeçalho interno autenticado por segredo compartilhado (InternalClientIpMiddleware).
        services.AddOptions<InternalOptions>().Bind(configuration.GetSection(InternalOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<InternalOptions>, InternalOptionsValidator>());

        // Corpo máximo de 256 KiB (limite do sync push, SEC-042) em todas as rotas da API.
        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
        {
            o.Limits.MaxRequestBodySize = 256 * 1024;
            o.AddServerHeader = false;
        });

        services.AddHostedService<MigrationHostedService>();
        return services;
    }

    /// <summary>Pipeline comum: IP do cliente vindo do BFF (autenticado), correlação/erros RFC 7807, autenticação e autorização.</summary>
    public static IApplicationBuilder UseNinaPipeline(this IApplicationBuilder app)
    {
        app.UseMiddleware<InternalClientIpMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<ProblemDetailsMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}

/// <summary>Aplica as migrações na inicialização quando <c>Database:ApplyMigrations=true</c> (conexão de dono em <c>ConnectionStrings:Migrations</c>).</summary>
public sealed class MigrationHostedService(IOptions<DatabaseOptions> options, IConfiguration configuration) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ApplyMigrations)
        {
            return;
        }

        var cs = configuration.GetConnectionString("Migrations")
                 ?? throw new InvalidOperationException("ConnectionStrings:Migrations não configurada.");
        var dir = options.Value.MigrationsPath
                  ?? MigrationRunner.FindDefaultDirectory(AppContext.BaseDirectory)
                  ?? throw new InvalidOperationException("Diretório de migrações não encontrado (Database:MigrationsPath).");
        await new MigrationRunner(cs, dir).ApplyAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
