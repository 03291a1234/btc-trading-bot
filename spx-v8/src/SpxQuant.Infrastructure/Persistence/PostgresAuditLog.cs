using Npgsql;
using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Audit;

namespace SpxQuant.Infrastructure.Persistence;

/// <summary>
/// Hash-chained audit log in PostgreSQL. Appends are serialized with a transaction-scoped advisory lock, so multiple
/// API processes can share one database without forking the chain. UPDATE/DELETE/TRUNCATE are blocked by trigger.
/// </summary>
public sealed class PostgresAuditLog(NpgsqlDataSource db, Func<DateTimeOffset> now) : IAuditLog
{
    private const long LockKey = 0x5350585141; // "SPXQA"

    public async Task<AuditRecord> AppendAsync(string kind, string? subject, object payload, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var l = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({LockKey})", conn, tx)) await l.ExecuteNonQueryAsync(ct);
        AuditRecord? last = null;
        await using (var q = new NpgsqlCommand("SELECT sequence, at, kind, subject, payload_json, previous_hash, hash FROM audit_log ORDER BY sequence DESC LIMIT 1", conn, tx))
        await using (var r = await q.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) last = Read(r);
        var record = AuditRecord.Create(last, now(), kind, subject, json);
        await using (var ins = new NpgsqlCommand("""
            INSERT INTO audit_log (sequence, at, kind, subject, payload_json, previous_hash, hash) VALUES (@s, @at, @k, @sub, @p, @prev, @h)
            """, conn, tx))
        {
            ins.Parameters.AddWithValue("s", record.Sequence);
            ins.Parameters.AddWithValue("at", record.At.UtcDateTime);
            ins.Parameters.AddWithValue("k", record.Kind);
            ins.Parameters.AddWithValue("sub", (object?)record.Subject ?? DBNull.Value);
            ins.Parameters.AddWithValue("p", record.PayloadJson);
            ins.Parameters.AddWithValue("prev", record.PreviousHash);
            ins.Parameters.AddWithValue("h", record.Hash);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<AuditRecord>> ReadAsync(long afterSequence, int limit, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT sequence, at, kind, subject, payload_json, previous_hash, hash FROM audit_log WHERE sequence > @a ORDER BY sequence LIMIT @n");
        cmd.Parameters.AddWithValue("a", afterSequence);
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 1000));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditRecord>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<string?> VerifyAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT sequence, at, kind, subject, payload_json, previous_hash, hash FROM audit_log ORDER BY sequence");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AuditRecord>();
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return AuditRecord.VerifyChain(list);
    }

    private static AuditRecord Read(NpgsqlDataReader r) => new(r.GetInt64(0), new DateTimeOffset(r.GetDateTime(1), TimeSpan.Zero), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6));
}
