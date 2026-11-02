using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Audit;
using SpxQuant.Domain.Calendar;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;

namespace SpxQuant.Application.Tests;

internal sealed class MutableClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; set; } = now; }

internal sealed class FakeCalendar : ITradingCalendarProvider
{
    public TradingCalendar Calendar { get; } = new(new(2026, 1, 1), new(2026, 12, 31),
        [new(new(2026, 11, 26), SessionStatus.Closed)], "test");
}

internal sealed class FakeMarket : IMarketDataProvider, IBarSource
{
    public List<OptionQuote> Chain { get; set; } = [];
    public IndexQuote? Index { get; set; }
    public List<Bar> Bars { get; set; } = [];
    public bool Fail { get; set; }
    public DataSourceKind Kind => DataSourceKind.TradierReadOnly;
    public Provenance DataProvenance => Provenance.Observed;
    public Task<IndexQuote?> GetIndexQuoteAsync(string symbol, CancellationToken ct) =>
        Fail ? throw new MarketDataUnavailableException("down") : Task.FromResult(Index);
    public Task<IReadOnlyList<OptionQuote>> GetOptionChainAsync(string u, DateOnly e, CancellationToken ct) =>
        Fail ? throw new MarketDataUnavailableException("down") : Task.FromResult<IReadOnlyList<OptionQuote>>(Chain);
    public Task<IReadOnlyList<Bar>> GetOneMinuteBarsAsync(string s, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Fail ? throw new MarketDataUnavailableException("down") : Task.FromResult<IReadOnlyList<Bar>>(Bars);
}

internal sealed class MemoryStore : IMarketDataStore
{
    public Dictionary<(string, Timeframe, DateTimeOffset), Bar> Bars { get; } = [];
    public List<ChainSnapshot> Snapshots { get; } = [];
    public Task<int> InsertBarsAsync(string symbol, Timeframe tf, IReadOnlyList<Bar> bars, string source, CancellationToken ct) =>
        Task.FromResult(bars.Count(b => Bars.TryAdd((symbol, tf, b.Start), b)));
    public Task<IReadOnlyList<Bar>> GetBarsAsync(string symbol, Timeframe tf, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Bar>>(Bars.Where(kv => kv.Key.Item1 == symbol && kv.Key.Item2 == tf && kv.Key.Item3 >= from && kv.Key.Item3 < to).Select(kv => kv.Value).OrderBy(b => b.Start).ToArray());
    public Task<StoredSnapshotInfo> SaveChainSnapshotAsync(ChainSnapshot s, CancellationToken ct)
    {
        Snapshots.Add(s);
        return Task.FromResult(new StoredSnapshotInfo(Snapshots.Count, s.Underlying, s.Expiration, s.CapturedAt, s.Source, s.Provenance.ToString(), s.Options.Count, s.Index?.Last, ""));
    }
    public Task<ChainSnapshot?> GetSnapshotAsOfAsync(string u, DateOnly e, DateTimeOffset asOf, CancellationToken ct) =>
        Task.FromResult(Snapshots.Where(s => s.CapturedAt <= asOf).OrderBy(s => s.CapturedAt).LastOrDefault());
    public Task<ChainSnapshot?> GetSnapshotAsync(long id, CancellationToken ct) => Task.FromResult<ChainSnapshot?>(Snapshots[(int)id - 1]);
    public Task<IReadOnlyList<StoredSnapshotInfo>> ListSnapshotsAsync(DateTimeOffset f, DateTimeOffset t, int n, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class MemoryAudit : IAuditLog
{
    public List<AuditRecord> Records { get; } = [];
    public Task<AuditRecord> AppendAsync(string kind, string? subject, object payload, CancellationToken ct)
    {
        var r = AuditRecord.Create(Records.LastOrDefault(), DateTimeOffset.UnixEpoch, kind, subject, System.Text.Json.JsonSerializer.Serialize(payload));
        Records.Add(r);
        return Task.FromResult(r);
    }
    public Task<IReadOnlyList<AuditRecord>> ReadAsync(long after, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditRecord>>(Records);
    public Task<string?> VerifyAsync(CancellationToken ct) => Task.FromResult(AuditRecord.VerifyChain(Records));
}
