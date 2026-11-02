using Npgsql;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;
using SpxQuant.Infrastructure.Persistence;
using SpxQuant.Infrastructure.Replay;
using Xunit;

namespace SpxQuant.Infrastructure.Tests;

/// <summary>
/// Integration tests against a real PostgreSQL/TimescaleDB. Set SPXQ_TEST_PG to a connection string for a DISPOSABLE
/// database (each test uses its own schema). Without it these tests report as Skipped, never as Passed.
/// </summary>
public sealed class PostgresTests : IAsyncLifetime
{
    private static readonly string? Cs = Environment.GetEnvironmentVariable("SPXQ_TEST_PG");
    private readonly string _schema = "t_" + Guid.NewGuid().ToString("N")[..12];
    private NpgsqlDataSource? _db;
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        if (Cs is null) return;
        await using (var admin = NpgsqlDataSource.Create(Cs))
        await using (var c = admin.CreateCommand($"CREATE SCHEMA {_schema}")) await c.ExecuteNonQueryAsync();
        _db = new NpgsqlDataSourceBuilder(Cs + $";Search Path={_schema},public").Build();
        await PostgresSchema.MigrateAsync(_db, default);
    }

    public async Task DisposeAsync()
    {
        if (_db is null) return;
        await _db.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(Cs!);
        await using var c = admin.CreateCommand($"DROP SCHEMA {_schema} CASCADE");
        await c.ExecuteNonQueryAsync();
    }

    private static Bar B(int minute, decimal px = 7580m) => new(T0.AddMinutes(minute), TimeSpan.FromMinutes(1), px, px + 1, px - 1, px + 0.5m, null);

    private static ChainSnapshot Snap(DateTimeOffset at, decimal last) => new("SPX", new(2026, 11, 2), at, "test", Provenance.Observed,
        new IndexQuote("SPX", last, at, at), [Fixtures.Quote(at)]);

    [SkippableFact] public async Task MigrationsAreIdempotent()
    {
        Skip.If(Cs is null, "SPXQ_TEST_PG not set");
        await PostgresSchema.MigrateAsync(_db!, default);
        await using var c = _db!.CreateCommand("SELECT count(*) FROM schema_migrations");
        Assert.Equal(1L, await c.ExecuteScalarAsync());
    }

    [SkippableFact] public async Task BarsInsertOnceAndNeverOverwrite()
    {
        Skip.If(Cs is null, "SPXQ_TEST_PG not set");
        var store = new PostgresMarketDataStore(_db!);
        Assert.Equal(2, await store.InsertBarsAsync("SPX", Timeframe.OneMinute, [B(0), B(1)], "test", default));
        Assert.Equal(1, await store.InsertBarsAsync("SPX", Timeframe.OneMinute, [B(1, 9999m), B(2)], "test", default));
        var bars = await store.GetBarsAsync("SPX", Timeframe.OneMinute, T0, T0.AddMinutes(10), default);
        Assert.Equal(3, bars.Count);
        Assert.Equal(7580m, bars[1].Open); // original kept
        await Assert.ThrowsAsync<ArgumentException>(() => store.InsertBarsAsync("SPX", Timeframe.FiveMinutes, [B(0)], "test", default));
    }

    [SkippableFact] public async Task DatabaseRejectsInconsistentBars()
    {
        Skip.If(Cs is null, "SPXQ_TEST_PG not set");
        var store = new PostgresMarketDataStore(_db!);
        await Assert.ThrowsAsync<PostgresException>(() =>
            store.InsertBarsAsync("SPX", Timeframe.OneMinute, [new Bar(T0, TimeSpan.FromMinutes(1), 100, 99, 98, 100, null)], "test", default));
    }

    [SkippableFact] public async Task SnapshotAsOfNeverReturnsTheFuture()
    {
        Skip.If(Cs is null, "SPXQ_TEST_PG not set");
        var store = new PostgresMarketDataStore(_db!);
        await store.SaveChainSnapshotAsync(Snap(T0, 7580m), default);
        await store.SaveChainSnapshotAsync(Snap(T0.AddSeconds(15), 7581m), default);
        Assert.Null(await store.GetSnapshotAsOfAsync("SPX", new(2026, 11, 2), T0.AddSeconds(-1), default));
        Assert.Equal(7580m, (await store.GetSnapshotAsOfAsync("SPX", new(2026, 11, 2), T0.AddSeconds(14), default))!.Index!.Last);
        var later = (await store.GetSnapshotAsOfAsync("SPX", new(2026, 11, 2), T0.AddMinutes(5), default))!;
        Assert.Equal(7581m, later.Index!.Last);
        Assert.Equal(Fixtures.Quote(T0).Symbol, later.Options[0].Symbol);
        Assert.Equal(0.80m, later.Options[0].Bid);
        Assert.Equal(2, (await store.ListSnapshotsAsync(T0, T0.AddMinutes(1), 10, default)).Count);
    }

    [SkippableFact] public async Task AlteredSnapshotPayloadIsDetected()
    {
        Skip.If(Cs is null, "SPXQ_TEST_PG not set");
        var store = new PostgresMarketDataStore(_db!);
        var info = await store.SaveChainSnapshotAsync(Snap(T0, 7580m), default);
        await using (var c = _db!.CreateCommand("UPDATE chain_snapshots SET payload = jsonb_set(payload, '{index,last}', '1')"))
            await c.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetSnapshotAsync(info.Id, default));
    }

    [SkippableFact] public async Task RecordedReplayRespectsTheReplayClock()
    {
        Skip.If(Cs is null, "SPXQ_TEST_PG not set");
        var store = new PostgresMarketDataStore(_db!);
        await store.SaveChainSnapshotAsync(Snap(T0, 7580m), default);
        await store.SaveChainSnapshotAsync(Snap(T0.AddMinutes(1), 7590m), default);
        await store.InsertBarsAsync("SPX", Timeframe.OneMinute, [B(0), B(1), B(2)], "test", default);
        var replay = new RecordedReplayProvider(store, new FixedClock(T0.AddSeconds(90)));
        Assert.Equal(7590m, (await replay.GetIndexQuoteAsync("SPX", default))!.Last);
        // At 15:01:30 only the 15:00 bar has closed; the 15:01 bar is forming and 15:02 is the future.
        var bars = await replay.GetOneMinuteBarsAsync("SPX", T0, T0.AddHours(1), default);
        Assert.Equal([T0], bars.Select(b => b.Start));
        var early = new RecordedReplayProvider(store, new FixedClock(T0.AddSeconds(-1)));
        Assert.Empty(await early.GetOptionChainAsync("SPX", new(2026, 11, 2), default));
    }

    [SkippableFact] public async Task AuditLogChainsAndIsAppendOnly()
    {
        Skip.If(Cs is null, "SPXQ_TEST_PG not set");
        var log = new PostgresAuditLog(_db!, () => DateTimeOffset.UtcNow); // sub-microsecond ticks must survive the round trip
        await log.AppendAsync("A", "x", new { n = 1 }, default);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => log.AppendAsync("B", null, new { i }, default)));
        Assert.Null(await log.VerifyAsync(default));
        Assert.Equal(Enumerable.Range(1, 11).Select(i => (long)i), (await log.ReadAsync(0, 100, default)).Select(r => r.Sequence));
        await using var c = _db!.CreateCommand("UPDATE audit_log SET kind = 'Z' WHERE sequence = 1");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => c.ExecuteNonQueryAsync());
        Assert.Contains("append-only", ex.MessageText);
    }
}
