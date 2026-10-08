using Npgsql;

namespace Nina.Family.Tests.Infrastructure;

/// <summary>Base dos testes de integração: um banco migrado (modelo) por classe e a API in-process conectada como <c>nina_app</c>.</summary>
[Collection(PostgresTestGroup.Name)]
public abstract class IntegrationTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    protected PostgresFixture Postgres => postgres;

    protected TestDatabase Database { get; private set; } = null!;

    protected ApiFactory Factory { get; private set; } = null!;

    protected ApiClient Api { get; private set; } = null!;

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

    /// <summary>Consulta como dono/superusuário (ignora RLS) para verificar o estado persistido.</summary>
    protected async Task<T?> AdminScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddRange(parameters);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    protected async Task AdminExecAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
