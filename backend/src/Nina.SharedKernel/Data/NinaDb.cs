using Npgsql;

namespace Nina.SharedKernel.Data;

/// <summary>Unit of work: uma transação por operação, com contexto de RLS opcional.</summary>
public sealed class NinaDb(NpgsqlDataSource dataSource)
{
    public async Task<T> InTransactionAsync<T>(Guid? userId, Func<DbTx, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var tx = new DbTx(connection, transaction, cancellationToken);
        if (userId is { } id)
        {
            await tx.SetUserAsync(id);
        }

        var result = await work(tx);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task InTransactionAsync(Guid? userId, Func<DbTx, Task> work, CancellationToken cancellationToken) =>
        InTransactionAsync<bool>(userId, async tx =>
        {
            await work(tx);
            return true;
        }, cancellationToken);

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand("SELECT 1", connection);
            await cmd.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }
}
