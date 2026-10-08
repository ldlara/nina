using Npgsql;
using NpgsqlTypes;

namespace Nina.SharedKernel.Data;

/// <summary>Transação aberta com conexão do papel <c>nina_app</c>. O contexto de RLS é definido por <c>SET LOCAL</c>.</summary>
public sealed class DbTx
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;

    internal DbTx(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        _connection = connection;
        _transaction = transaction;
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }

    /// <summary>Define <c>nina.user_id</c> (<c>SET LOCAL</c> via <c>set_config</c>) para o resto da transação (RLS). Sem isso nenhuma linha por usuário é visível.</summary>
    public async Task SetUserAsync(Guid userId)
    {
        // Equivalente a SET LOCAL (is_local = true) e parametrizado (SR-008): vale só até o fim da transação e nada é
        // concatenado em SQL. Nunca usar SET de sessão: o contexto vazaria entre requisições no pool.
        await ExecAsync("SELECT set_config('nina.user_id', @id, true)", Db.Text("id", userId.ToString("D")));
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

    public async Task<T?> QueryFirstAsync<T>(string sql, Func<NpgsqlDataReader, T> map, params NpgsqlParameter[] parameters)
        where T : class
    {
        await using var cmd = Create(sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(CancellationToken);
        return await reader.ReadAsync(CancellationToken) ? map(reader) : null;
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

/// <summary>Atalhos para criar parâmetros tipados.</summary>
public static class Db
{
    public static NpgsqlParameter P(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    public static NpgsqlParameter P(string name, object? value, NpgsqlDbType type) =>
        new(name, type) { Value = value ?? DBNull.Value };

    public static NpgsqlParameter Uuid(string name, Guid? value) => P(name, value, NpgsqlDbType.Uuid);

    public static NpgsqlParameter Text(string name, string? value) => P(name, value, NpgsqlDbType.Text);

    public static NpgsqlParameter Timestamp(string name, DateTimeOffset? value) =>
        P(name, value?.UtcDateTime, NpgsqlDbType.TimestampTz);

    public static NpgsqlParameter Bytes(string name, byte[]? value) => P(name, value, NpgsqlDbType.Bytea);
}
