using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;

namespace SpxQuant.Infrastructure.Persistence;

public sealed class PostgresMarketDataStore(NpgsqlDataSource db) : IMarketDataStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private sealed record Payload(IndexQuote? Index, IReadOnlyList<OptionQuote> Options);

    public async Task<int> InsertBarsAsync(string symbol, Timeframe timeframe, IReadOnlyList<Bar> bars, string source, CancellationToken ct)
    {
        if (bars.Count == 0) return 0;
        if (bars.Any(b => b.Duration != timeframe.Duration())) throw new ArgumentException("Bar duration does not match timeframe");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var inserted = 0;
        foreach (var b in bars)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO bars (symbol, timeframe, bar_start, open, high, low, close, volume, source, provenance)
                VALUES (@s, @tf, @t, @o, @h, @l, @c, @v, @src, 'Observed')
                ON CONFLICT (symbol, timeframe, bar_start) DO NOTHING
                """, conn, tx);
            cmd.Parameters.AddWithValue("s", symbol);
            cmd.Parameters.AddWithValue("tf", timeframe.Code());
            cmd.Parameters.AddWithValue("t", b.Start.ToUniversalTime());
            cmd.Parameters.AddWithValue("o", b.Open); cmd.Parameters.AddWithValue("h", b.High);
            cmd.Parameters.AddWithValue("l", b.Low); cmd.Parameters.AddWithValue("c", b.Close);
            cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Bigint) { Value = (object?)b.Volume ?? DBNull.Value });
            cmd.Parameters.AddWithValue("src", source);
            inserted += await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task<IReadOnlyList<Bar>> GetBarsAsync(string symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT bar_start, open, high, low, close, volume FROM bars
            WHERE symbol = @s AND timeframe = @tf AND bar_start >= @from AND bar_start < @to ORDER BY bar_start
            """);
        cmd.Parameters.AddWithValue("s", symbol);
        cmd.Parameters.AddWithValue("tf", timeframe.Code());
        cmd.Parameters.AddWithValue("from", from.ToUniversalTime());
        cmd.Parameters.AddWithValue("to", to.ToUniversalTime());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<Bar>();
        while (await r.ReadAsync(ct))
            list.Add(new Bar(new DateTimeOffset(r.GetDateTime(0), TimeSpan.Zero), timeframe.Duration(), r.GetDecimal(1), r.GetDecimal(2),
                r.GetDecimal(3), r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetInt64(5)));
        return list;
    }

    public async Task<StoredSnapshotInfo> SaveChainSnapshotAsync(ChainSnapshot s, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new Payload(s.Index, s.Options), Json);
        var sha = ChainSnapshot.Sha256(payload);
        await using var cmd = db.CreateCommand("""
            INSERT INTO chain_snapshots (underlying, expiration, captured_at, source, provenance, option_count, index_last, content_sha256, payload)
            VALUES (@u, @e, @t, @src, @p, @n, @last, @sha, @payload::jsonb) RETURNING id
            """);
        cmd.Parameters.AddWithValue("u", s.Underlying);
        cmd.Parameters.AddWithValue("e", s.Expiration);
        cmd.Parameters.AddWithValue("t", s.CapturedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("src", s.Source);
        cmd.Parameters.AddWithValue("p", s.Provenance.ToString());
        cmd.Parameters.AddWithValue("n", s.Options.Count);
        cmd.Parameters.Add(new NpgsqlParameter("last", NpgsqlDbType.Numeric) { Value = (object?)s.Index?.Last ?? DBNull.Value });
        cmd.Parameters.AddWithValue("sha", sha);
        cmd.Parameters.AddWithValue("payload", payload);
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return new(id, s.Underlying, s.Expiration, s.CapturedAt, s.Source, s.Provenance.ToString(), s.Options.Count, s.Index?.Last, sha);
    }

    public Task<ChainSnapshot?> GetSnapshotAsOfAsync(string underlying, DateOnly expiration, DateTimeOffset asOf, CancellationToken ct) =>
        ReadOneAsync("WHERE underlying = @u AND expiration = @e AND captured_at <= @t ORDER BY captured_at DESC LIMIT 1",
            p => { p.AddWithValue("u", underlying); p.AddWithValue("e", expiration); p.AddWithValue("t", asOf.ToUniversalTime()); }, ct);

    public Task<ChainSnapshot?> GetSnapshotAsync(long id, CancellationToken ct) =>
        ReadOneAsync("WHERE id = @id", p => p.AddWithValue("id", id), ct);

    public async Task<IReadOnlyList<StoredSnapshotInfo>> ListSnapshotsAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, underlying, expiration, captured_at, source, provenance, option_count, index_last, content_sha256
            FROM chain_snapshots WHERE captured_at >= @from AND captured_at < @to ORDER BY captured_at LIMIT @n
            """);
        cmd.Parameters.AddWithValue("from", from.ToUniversalTime());
        cmd.Parameters.AddWithValue("to", to.ToUniversalTime());
        cmd.Parameters.AddWithValue("n", Math.Clamp(limit, 1, 5000));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<StoredSnapshotInfo>();
        while (await r.ReadAsync(ct))
            list.Add(new(r.GetInt64(0), r.GetString(1), DateOnly.FromDateTime(r.GetDateTime(2)), new DateTimeOffset(r.GetDateTime(3), TimeSpan.Zero),
                r.GetString(4), r.GetString(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetDecimal(7), r.GetString(8)));
        return list;
    }

    private async Task<ChainSnapshot?> ReadOneAsync(string where, Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "SELECT underlying, expiration, captured_at, source, provenance, content_sha256, payload::text FROM chain_snapshots " + where);
        bind(cmd.Parameters);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        // jsonb normalizes key order/whitespace, so integrity is checked on the re-serialized payload, not the raw text.
        var payload = JsonSerializer.Deserialize<Payload>(r.GetString(6), Json) ?? throw new InvalidDataException("Empty snapshot payload");
        if (ChainSnapshot.Sha256(JsonSerializer.Serialize(payload, Json)) != r.GetString(5))
            throw new InvalidDataException("Chain snapshot content hash mismatch; stored data was altered");
        return new ChainSnapshot(r.GetString(0), DateOnly.FromDateTime(r.GetDateTime(1)), new DateTimeOffset(r.GetDateTime(2), TimeSpan.Zero),
            r.GetString(3), Enum.Parse<Provenance>(r.GetString(4)), payload.Index, payload.Options);
    }
}
