using System.Data;
using System.Text.Json.Nodes;
using Nina.SharedKernel.Data;
using Npgsql;

namespace Nina.Tracking.Persistence;

/// <summary>
/// Transação do papel <c>nina_app</c> com o contexto de RLS (<c>nina.user_id</c>, <c>nina.device_id</c>) definido por
/// <c>set_config(..., true)</c> (equivale a <c>SET LOCAL</c>: vale só até o fim da transação, nunca vaza no pool). Ao contrário do
/// <c>DbTx</c> genérico do SharedKernel, aceita nível de isolamento (o pull usa <c>REPEATABLE READ</c>) e <c>SAVEPOINT</c> (o push
/// isola cada mutação para que uma rejeição não desfaça as demais).
/// </summary>
internal sealed class TrackingTx : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private int _savepoints;

    private TrackingTx(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        _connection = connection;
        _transaction = transaction;
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }

    public static async Task<TrackingTx> BeginAsync(
        NpgsqlDataSource dataSource, Guid userId, Guid? deviceId, IsolationLevel isolation, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(isolation, cancellationToken);
            var tx = new TrackingTx(connection, transaction, cancellationToken);
            await tx.ExecAsync(
                "SELECT set_config('nina.user_id', @u, true), set_config('nina.device_id', @d, true)",
                Db.Text("u", userId.ToString("D")),
                Db.Text("d", deviceId?.ToString("D") ?? string.Empty));
            return tx;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<int> ExecAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = Create(sql, parameters);
        return await cmd.ExecuteNonQueryAsync(CancellationToken);
    }

    public async Task<T?> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = Create(sql, parameters);
        var value = await cmd.ExecuteScalarAsync(CancellationToken);
        return value is null or DBNull ? default : (T)value;
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map, params NpgsqlParameter[] parameters)
    {
        var result = new List<T>();
        await using var cmd = Create(sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(CancellationToken);
        while (await reader.ReadAsync(CancellationToken))
        {
            result.Add(map(reader));
        }

        return result;
    }

    public async Task<T?> QueryFirstAsync<T>(string sql, Func<NpgsqlDataReader, T> map, params NpgsqlParameter[] parameters)
        where T : class
    {
        await using var cmd = Create(sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(CancellationToken);
        return await reader.ReadAsync(CancellationToken) ? map(reader) : null;
    }

    /// <summary>Consulta que devolve uma coluna jsonb por linha (<c>to_jsonb(linha)</c>) já como objeto JSON.</summary>
    public Task<List<JsonObject>> QueryJsonAsync(string sql, params NpgsqlParameter[] parameters) =>
        QueryAsync(sql, r => (JsonObject)JsonNode.Parse(r.GetFieldValue<string>(0))!, parameters);

    public async Task<JsonObject?> QueryJsonFirstAsync(string sql, params NpgsqlParameter[] parameters) =>
        (await QueryJsonAsync(sql, parameters)).FirstOrDefault();

    public async Task<string> SavepointAsync()
    {
        var name = "sp" + (++_savepoints).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ExecAsync("SAVEPOINT " + name);
        return name;
    }

    public Task RollbackToAsync(string savepoint) => ExecAsync("ROLLBACK TO SAVEPOINT " + savepoint);

    public Task ReleaseAsync(string savepoint) => ExecAsync("RELEASE SAVEPOINT " + savepoint);

    public Task CommitAsync() => _transaction.CommitAsync(CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private NpgsqlCommand Create(string sql, NpgsqlParameter[] parameters)
    {
        var cmd = new NpgsqlCommand(sql, _connection, _transaction);
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p);
        }

        return cmd;
    }
}
