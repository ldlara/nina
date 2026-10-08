using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nina.Identity.Tests.Infrastructure;
using Nina.SharedKernel.Data;
using Npgsql;

namespace Nina.Identity.Tests.Integration;

/// <summary>Infra compartilhada: papel nina_app + RLS, auditoria, erros RFC 7807 e migração.</summary>
public sealed class PlatformTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    [Fact]
    public async Task The_application_connects_as_nina_app_not_as_superuser_or_owner()
    {
        await Api.RegisterAndVerifyAsync();
        var dataSource = Factory.Services.GetRequiredService<NpgsqlDataSource>();
        var builder = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);

        Assert.Equal("nina_app", builder.Username);
        Assert.True(await AdminScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname = @d AND usename = 'nina_app'", new NpgsqlParameter("d", Database.Name)) > 0);
        Assert.False(await AdminScalarAsync<bool>("SELECT rolsuper FROM pg_roles WHERE rolname = 'nina_app'"));
    }

    [Fact]
    public async Task Row_level_security_hides_other_users_sessions_and_everything_without_context()
    {
        var a = await Api.RegisterAndVerifyAsync();
        var b = await Api.RegisterAndVerifyAsync();
        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync();

        Assert.Equal(0, await CountAsync(connection, null));
        Assert.Equal(1, await CountAsync(connection, a.UserId));
        Assert.Equal(1, await CountAsync(connection, b.UserId));
        Assert.Equal(2, await AdminScalarAsync<long>("SELECT count(*) FROM nina.auth_session"));

        static async Task<long> CountAsync(NpgsqlConnection c, Guid? user)
        {
            await using var tx = await c.BeginTransactionAsync();
            if (user is { } u)
            {
                await using var set = new NpgsqlCommand($"SET LOCAL nina.user_id = '{u}'", c, tx);
                await set.ExecuteNonQueryAsync();
            }

            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM nina.auth_session", c, tx);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }
    }

    [Fact]
    public async Task The_user_context_does_not_leak_between_pooled_requests()
    {
        var a = await Api.RegisterAndVerifyAsync();
        var b = await Api.RegisterAndVerifyAsync();
        for (var i = 0; i < 10; i++)
        {
            var listA = await Api.GetAsync("/v1/me/sessions", a.AccessToken);
            var listB = await Api.GetAsync("/v1/me/sessions", b.AccessToken);
            Assert.Equal(a.SessionId.ToString(), Assert.Single(listA.Json!["items"]!.AsArray())!["id"]!.GetValue<string>());
            Assert.Equal(b.SessionId.ToString(), Assert.Single(listB.Json!["items"]!.AsArray())!["id"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Audit_records_signup_login_and_failures_with_hashed_ip_and_no_pii()
    {
        var email = ApiClient.NewEmail();
        var s = await Api.RegisterAndVerifyAsync(email);
        await Api.PostAsync(
            "/v1/auth/login",
            new JsonObject { ["email"] = email, ["password"] = "wrong-password-123", ["device"] = ApiClient.Device() },
            tweak: r => r.Headers.Add("X-Forwarded-For", "203.0.113.77"));

        async Task<long> Count(string action, string? result = null) => await AdminScalarAsync<long>(
            "SELECT count(*) FROM nina.audit_event WHERE action = @a AND (@r::text IS NULL OR result = @r)",
            new NpgsqlParameter("a", action), new NpgsqlParameter("r", (object?)result ?? DBNull.Value) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });

        Assert.Equal(1, await Count("auth.register"));
        Assert.Equal(1, await Count("auth.email_verified"));
        Assert.Equal(1, await Count("auth.login_failed", "FAILURE"));
        var failureActor = await AdminScalarAsync<Guid>("SELECT actor_user_id FROM nina.audit_event WHERE action = 'auth.login_failed'");
        Assert.Equal(s.UserId, failureActor);
        var ipHash = await AdminScalarAsync<byte[]>("SELECT ip_hash FROM nina.audit_event WHERE action = 'auth.login_failed'");
        Assert.Equal(32, ipHash!.Length);
        Assert.DoesNotContain("203.0.113.77", Encoding.UTF8.GetString(ipHash), StringComparison.Ordinal);
        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE metadata_safe::text ILIKE '%@%' OR metadata_safe::text ILIKE '%password%' OR metadata_safe::text ILIKE '%token%'"));
        Assert.NotNull(await AdminScalarAsync<string>("SELECT request_id FROM nina.audit_event WHERE action = 'auth.login_failed'"));
    }

    [Fact]
    public async Task Audit_events_are_append_only_for_the_application_role()
    {
        await Api.RegisterAndVerifyAsync();
        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync();

        await using var read = new NpgsqlCommand("SELECT count(*) FROM nina.audit_event", connection);
        await Assert.ThrowsAsync<PostgresException>(() => read.ExecuteScalarAsync()); // sem SELECT para nina_app
        await using var delete = new NpgsqlCommand("DELETE FROM nina.audit_event", connection);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Unknown_routes_and_bad_bodies_return_problem_json_with_request_id()
    {
        var notFound = await Api.GetAsync("/v1/does-not-exist");
        var malformed = await Api.SendAsync(HttpMethod.Post, "/v1/auth/login", null, tweak: r =>
            r.Content = new StringContent("{ not json", Encoding.UTF8, "application/json"));
        var echoed = await Api.GetAsync("/v1/me", tweak: null);

        Assert.Equal(HttpStatusCode.NotFound, notFound.Status);
        Assert.True(notFound.IsProblem);
        Assert.Equal("NOT_FOUND", notFound.Code);
        Assert.NotNull(notFound.Json!["request_id"]);
        Assert.True(notFound.Headers.Contains("X-Request-Id"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.Status);
        Assert.Equal("VALIDATION_FAILED", malformed.Code);
        Assert.Equal("https://api.nina.app/problems/validation-failed", malformed.Json!["type"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, echoed.Status);
    }

    [Fact]
    public async Task A_caller_supplied_request_id_is_echoed_and_unsafe_ones_are_replaced()
    {
        var ok = await Api.SendAsync(HttpMethod.Get, "/v1/me", null, tweak: r => r.Headers.Add("X-Request-Id", "client-req_123"));
        var bad = await Api.SendAsync(HttpMethod.Get, "/v1/me", null, tweak: r => r.Headers.Add("X-Request-Id", "<script>alert(1)</script>"));

        Assert.Equal("client-req_123", ok.Json!["request_id"]!.GetValue<string>());
        Assert.Equal("client-req_123", ok.Headers.GetValues("X-Request-Id").Single());
        Assert.DoesNotContain("<", bad.Json!["request_id"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_failures_return_a_generic_500_without_leaking_internals()
    {
        var badDb = new NpgsqlConnectionStringBuilder(Database.AppConnectionString) { Port = 1, Timeout = 2, Pooling = false }.ConnectionString;
        await using var factory = new ApiFactory(badDb);
        var client = new ApiClient(factory.CreateClient(), factory);

        var response = await client.PostAsync("/v1/auth/login", new JsonObject
        {
            ["email"] = "someone@example.org", ["password"] = "irrelevant-password", ["device"] = ApiClient.Device(),
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
        Assert.Equal("INTERNAL_ERROR", response.Code);
        var text = response.Json!.ToJsonString();
        foreach (var leaked in new[] { "Npgsql", "Exception", "SELECT", "127.0.0.1", "at Nina.", "nina_app" })
        {
            Assert.DoesNotContain(leaked, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Migration_runner_applies_0001_once_and_is_idempotent()
    {
        var name = "nina_mig_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = new NpgsqlConnection(Postgres.AdminConnectionString("postgres")))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var dir = MigrationRunner.FindDefaultDirectory(AppContext.BaseDirectory)!;
        var runner = new MigrationRunner(Postgres.AdminConnectionString(name), dir);
        var first = await runner.ApplyAsync(CancellationToken.None);
        var second = await runner.ApplyAsync(CancellationToken.None);

        Assert.Equal(["0001"], first);
        Assert.Empty(second);
        await using var check = new NpgsqlConnection(Postgres.AdminConnectionString(name));
        await check.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM nina.schema_migration WHERE version = '0001'", check);
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());
    }
}
