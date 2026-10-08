using Npgsql;

namespace Nina.SharedKernel.Data;

/// <summary>
/// Aplica os arquivos <c>NNNN_*.sql</c> de um diretório, em ordem, uma única vez (registro em <c>nina.schema_migration</c>).
/// Deve conectar com o dono do schema (nunca com <c>nina_app</c>); a migração <c>0001</c> cria o schema e os papéis.
/// </summary>
public sealed class MigrationRunner(string adminConnectionString, string directory)
{
    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken cancellationToken)
    {
        var files = Directory.GetFiles(directory, "*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        var applied = new List<string>();

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Serializa execuções concorrentes (várias instâncias subindo juntas).
        await ExecAsync(connection, "SELECT pg_advisory_lock(727001)", cancellationToken);
        try
        {
            var done = await LoadAppliedAsync(connection, cancellationToken);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var version = name.Split('_', 2)[0];
                if (done.Contains(version))
                {
                    continue;
                }

                var sql = await File.ReadAllTextAsync(file, cancellationToken);
                await ExecAsync(connection, sql, cancellationToken);
                applied.Add(version);
            }
        }
        finally
        {
            await ExecAsync(connection, "SELECT pg_advisory_unlock(727001)", CancellationToken.None);
        }

        return applied;
    }

    /// <summary>Localiza <c>backend/db/migrations</c> subindo a partir de um diretório base.</summary>
    public static string? FindDefaultDirectory(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "db", "migrations");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(dir.FullName, "backend", "db", "migrations");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static async Task<HashSet<string>> LoadAppliedAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass('nina.schema_migration') IS NOT NULL", connection);
        if (!(bool)(await exists.ExecuteScalarAsync(ct))!)
        {
            return [];
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT version FROM nina.schema_migration", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
