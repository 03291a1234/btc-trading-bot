using Npgsql;

namespace SpxQuant.Infrastructure.Persistence;

/// <summary>Applies embedded, numbered SQL migrations exactly once, under an advisory lock.</summary>
public static class PostgresSchema
{
    private const long LockKey = 0x5350585138; // "SPXQ8"

    public static async Task MigrateAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var l = new NpgsqlCommand($"SELECT pg_advisory_lock({LockKey})", conn)) await l.ExecuteNonQueryAsync(ct);
        try
        {
            await using (var c = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS schema_migrations (version integer PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", conn))
                await c.ExecuteNonQueryAsync(ct);
            var asm = typeof(PostgresSchema).Assembly;
            var scripts = asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal))
                .Select(n => (Name: n, Version: int.Parse(n.Split('.')[^2].Split('_')[0]))).OrderBy(x => x.Version);
            foreach (var (name, version) in scripts)
            {
                await using (var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE version = @v", conn))
                {
                    check.Parameters.AddWithValue("v", version);
                    if (await check.ExecuteScalarAsync(ct) is not null) continue;
                }
                using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
                var sql = await reader.ReadToEndAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var run = new NpgsqlCommand(sql, conn, tx)) await run.ExecuteNonQueryAsync(ct);
                await using (var mark = new NpgsqlCommand("INSERT INTO schema_migrations (version, name) VALUES (@v, @n)", conn, tx))
                {
                    mark.Parameters.AddWithValue("v", version); mark.Parameters.AddWithValue("n", name);
                    await mark.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await using var u = new NpgsqlCommand($"SELECT pg_advisory_unlock({LockKey})", conn);
            await u.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
