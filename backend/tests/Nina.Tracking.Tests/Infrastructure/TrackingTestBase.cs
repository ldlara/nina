using System.Text.Json.Nodes;
using Npgsql;

namespace Nina.Tracking.Tests.Infrastructure;

/// <summary>
/// Base dos testes de integração do Tracking: um banco migrado (modelo) por teste e a API in-process conectada como <c>nina_app</c> (sujeita a RLS).
/// Usuários nascem pela API (cadastro + e-mail verificado, token real); bebês e vínculos são montados direto no banco pelo dono, como pede o
/// BE-003/004 (o módulo Family não é dependência destes testes).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public abstract class TrackingTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    protected PostgresFixture Postgres => postgres;

    protected TestDatabase Database { get; private set; } = null!;

    protected ApiFactory Factory { get; private set; } = null!;

    protected ApiClient Api { get; private set; } = null!;

    /// <summary>Instante base dos eventos (sem frações, no passado recente) para as asserções serem determinísticas.</summary>
    protected static DateTimeOffset T0 { get; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds());

    public virtual async Task InitializeAsync()
    {
        Database = await postgres.CreateDatabaseAsync();
        Factory = new ApiFactory(Database.AppConnectionString, Configure);
        Api = new ApiClient(Factory.CreateClient(), Factory);
    }

    public virtual async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(postgres.AdminConnectionString("postgres"));
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {Database.Name} WITH (FORCE)", admin);
        await cmd.ExecuteNonQueryAsync();
    }

    protected virtual void Configure(Dictionary<string, string?> settings)
    {
    }

    // ------------------------------------------------------------------ cenário

    protected Task<Session> UserAsync(Guid? deviceId = null) => Api.RegisterAndVerifyAsync(deviceId: deviceId);

    /// <summary>Segundo aparelho do mesmo usuário (login com outro <c>device_id</c>).</summary>
    protected Task<Session> SecondDeviceAsync(Session user) => Api.LoginAsync(user.Email, user.Password, Guid.NewGuid());

    /// <summary>Cria família, bebê e vínculo OWNER ativo (como o dono do schema, ignorando RLS).</summary>
    protected async Task<Guid> BabyAsync(Session owner, string timezone = "America/Sao_Paulo", string name = "Bebe")
    {
        var family = Guid.NewGuid();
        var baby = Guid.NewGuid();
        await AdminExecAsync(
            $"""
            INSERT INTO nina.family (id, owner_user_id) VALUES ('{family}', '{owner.UserId}');
            INSERT INTO nina.baby (id, family_id, display_name, birth_date, due_date, timezone, created_by, last_modified_by)
              VALUES ('{baby}', '{family}', '{name}', current_date - 100, current_date - 90, '{timezone}', '{owner.UserId}', '{owner.UserId}');
            INSERT INTO nina.caregiver_membership (baby_id, user_id, role, status, accepted_at)
              VALUES ('{baby}', '{owner.UserId}', 'OWNER', 'ACTIVE', now());
            """);
        return baby;
    }

    /// <summary>Vínculo ativo de <paramref name="user"/> com o bebê no papel dado; devolve o id do vínculo.</summary>
    protected async Task<Guid> MemberAsync(Guid baby, Session user, string role)
    {
        var id = Guid.NewGuid();
        await AdminExecAsync(
            $"INSERT INTO nina.caregiver_membership (id, baby_id, user_id, role, status, accepted_at) VALUES ('{id}', '{baby}', '{user.UserId}', '{role}', 'ACTIVE', now())");
        return id;
    }

    /// <summary>Revoga o vínculo como o Owner (função de negócio <c>nina.remove_member</c>, com o contexto do Owner).</summary>
    protected async Task RemoveMemberAsync(Guid membership, Session owner)
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var ctx = new NpgsqlCommand($"SELECT set_config('nina.user_id', '{owner.UserId}', true)", connection, tx))
        {
            await ctx.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand($"SELECT nina.remove_member('{membership}')", connection, tx))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    // ------------------------------------------------------------------ banco (dono: ignora RLS, só para montar e inspecionar)

    protected async Task AdminExecAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    protected async Task<T?> AdminScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddRange(parameters);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    protected async Task<List<object?[]>> AdminRowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Altera um parâmetro de configuração como o dono (a trilha <c>config_change</c> é gravada pelo gatilho).</summary>
    protected Task SetParameterAsync(string key, string jsonValue, string type) =>
        AdminExecAsync(
            "INSERT INTO nina.app_parameter (param_key, value, value_type, description) VALUES (@k, @v::jsonb, @t, 'teste') " +
            "ON CONFLICT (param_key) DO UPDATE SET value = EXCLUDED.value",
            new NpgsqlParameter("k", key), new NpgsqlParameter("v", jsonValue), new NpgsqlParameter("t", type));

    // ------------------------------------------------------------------ chamadas

    protected Task<ApiResponse> PushAsync(Session s, params JsonObject[] mutations) => PushAsync(s, s.DeviceId, mutations);

    protected Task<ApiResponse> PushAsync(Session s, Guid deviceId, params JsonObject[] mutations) =>
        Api.PostAsync("/v1/sync/push", Mut.Push(deviceId, mutations), s.AccessToken);

    protected Task<ApiResponse> PullAsync(Session s, Guid baby, string? cursor = null, int? limit = null) =>
        Api.GetAsync(Mut.PullPath(baby, cursor, limit), s.AccessToken);

    /// <summary>Envia e exige 200; devolve os resultados por mutação.</summary>
    protected async Task<JsonArray> PushOkAsync(Session s, params JsonObject[] mutations)
    {
        var response = await PushAsync(s, mutations);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.Status);
        OpenApiContract.AssertValid("PushResponse", response.Json);          // teste de contrato: toda resposta de push confere com o OpenAPI
        return response.Json!["results"]!.AsArray();
    }

    /// <summary>Snapshot completo (todas as páginas) seguido de delta até esgotar; devolve todas as mudanças e o último cursor.</summary>
    protected async Task<(List<JsonNode> Changes, string Cursor)> PullAllAsync(Session s, Guid baby, string? cursor = null, int limit = 500)
    {
        var changes = new List<JsonNode>();
        var current = cursor;
        for (var i = 0; i < 200; i++)
        {
            var page = await PullAsync(s, baby, current, limit);
            Assert.Equal(System.Net.HttpStatusCode.OK, page.Status);
            OpenApiContract.AssertPullResponse(page.Json);              // teste de contrato: toda página de pull confere com o OpenAPI
            foreach (var c in page.Json!["changes"]!.AsArray())
            {
                changes.Add(c!.DeepClone());
            }

            current = page.Json["next_cursor"]!.GetValue<string>();
            if (!page.Json["has_more"]!.GetValue<bool>())
            {
                return (changes, current);
            }
        }

        throw new InvalidOperationException("pull não terminou");
    }
}
