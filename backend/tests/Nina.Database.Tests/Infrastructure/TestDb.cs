using Npgsql;
using NpgsqlTypes;

namespace Nina.Database.Tests.Infrastructure;

/// <summary>Sessão transacional como um papel, com o contexto de usuário (<c>nina.user_id</c>) definido como a aplicação faz.</summary>
public sealed class Session : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    private Session(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public static async Task<Session> OpenAsync(string connectionString, Guid? user)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        var session = new Session(connection, transaction);
        if (user is { } id)
        {
            await session.ExecAsync("SELECT set_config('nina.user_id', @u, true)", new NpgsqlParameter("u", id.ToString("D")));
        }

        return session;
    }

    public async Task<int> ExecAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = Create(sql, parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = Create(sql, parameters);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    public async Task<List<object?[]>> RowsAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = Create(sql, parameters);
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

    public Task CommitAsync() => _transaction.CommitAsync();

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private NpgsqlCommand Create(string sql, NpgsqlParameter[] parameters)
    {
        var cmd = new NpgsqlCommand(sql, _connection, _transaction);
        cmd.Parameters.AddRange(parameters);
        return cmd;
    }
}

/// <summary>Um banco novo (cópia do modelo migrado) por teste.</summary>
public abstract class DbTestBase(PgCluster cluster) : IAsyncLifetime
{
    protected PgCluster Cluster => cluster;

    protected string DatabaseName { get; private set; } = null!;

    protected World W { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        DatabaseName = await cluster.CreateDatabaseAsync();
        W = await World.CreateAsync(this);
    }

    public virtual Task DisposeAsync() => cluster.DropDatabaseAsync(DatabaseName);

    public Task<Session> OpenAsync(Role role, Guid? user = null) =>
        Session.OpenAsync(cluster.ConnectionString(role, DatabaseName), user);

    public Task<Session> AsApp(Guid? user) => OpenAsync(Role.App, user);

    /// <summary>Executa como o dono (ignora RLS e triggers de fichas não se aplicam): só para montar cenário e inspecionar.</summary>
    public async Task OwnerAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var s = await OpenAsync(Role.Owner);
        await s.ExecAsync(sql, parameters);
        await s.CommitAsync();
    }

    public async Task<T?> OwnerScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var s = await OpenAsync(Role.Owner);
        return await s.ScalarAsync<T>(sql, parameters);
    }

    /// <summary>
    /// Executa <paramref name="sql"/> numa transação própria como <paramref name="role"/> e devolve o SQLSTATE do erro (ou null se passou).
    /// A transação nunca é confirmada.
    /// </summary>
    public async Task<string?> FailsAsync(Role role, Guid? user, string sql, params NpgsqlParameter[] parameters)
    {
        await using var s = await OpenAsync(role, user);
        try
        {
            await s.ExecAsync(sql, parameters);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    /// <summary>Como <see cref="FailsAsync"/>, mas também tenta o COMMIT (para constraints diferidas) e confirma de fato quando passa.</summary>
    public async Task<string?> FailsOnCommitAsync(Role role, Guid? user, params string[] statements)
    {
        await using var s = await OpenAsync(role, user);
        try
        {
            foreach (var sql in statements)
            {
                await s.ExecAsync(sql);
            }

            await s.CommitAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    /// <summary>Cria (como dono) uma sessão ativa do usuário: a emissão de reautenticação exige uma sessão do próprio usuário.</summary>
    public async Task<Guid> SessionAsync(Guid user)
    {
        var id = Guid.NewGuid();
        await OwnerAsync(
            $"INSERT INTO nina.auth_session (id, user_id, device_id, platform, absolute_expires_at) VALUES ('{id}', '{user}', gen_random_uuid(), 'IOS', now() + interval '30 days')");
        return id;
    }

    /// <summary>
    /// Emite, como a API faz (nina.reauth_issue, com o MAC do servidor), um jti de reautenticação no livro-razão e devolve o hash (sem consumi-lo).
    /// <paramref name="scopes"/> vazio = token de transição v1.x.
    /// </summary>
    public async Task<byte[]> IssueReauthAsync(Guid user, Guid session, string[] scopes, string seed = "jti", TimeSpan? lifetime = null)
    {
        var hash = Hash(seed + Guid.NewGuid());
        var issued = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var expires = issued + (lifetime ?? TimeSpan.FromMinutes(5));
        var mac = ServerSigner.Sign("reauth.issue", ServerSigner.ReauthIssue(user, session, hash, scopes, issued, expires));
        await using var s = await AsApp(user);
        await s.ExecAsync(
            "SELECT nina.reauth_issue(@h, @s, @scopes, @iat, @exp, @mac)",
            Bytes("h", hash), P("s", session), new NpgsqlParameter("scopes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = scopes },
            P("iat", issued.UtcDateTime), P("exp", expires.UtcDateTime), Bytes("mac", mac));
        await s.CommitAsync();
        return hash;
    }

    /// <summary>Emite (assinado) e consome, como o Identity faz, um jti de reautenticação do escopo dado e devolve o hash para vincular a um recurso.</summary>
    public async Task<byte[]> ReauthAsync(Guid user, string scope, string seed = "jti")
    {
        var session = await SessionAsync(user);
        var hash = await IssueReauthAsync(user, session, [scope], seed);
        await using var s = await AsApp(user);
        Assert.True(await s.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, @scope, @s)", Bytes("h", hash), P("scope", scope), P("s", session)));
        await s.CommitAsync();
        return hash;
    }

    public static NpgsqlParameter P(string name, object? value) => new(name, value ?? DBNull.Value);

    public static NpgsqlParameter Bytes(string name, byte[] value) => new(name, NpgsqlDbType.Bytea) { Value = value };

    public static byte[] Hash(string seed) => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
}
