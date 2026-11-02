using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;

namespace SpxQuant.Infrastructure.Replay;

/// <summary>
/// Replays RECORDED data from the store against a replay clock. Every read is as-of the clock: the latest snapshot captured
/// at or before "now", and only bars confirmed by "now". It can never return data from the future.
/// </summary>
public sealed class RecordedReplayProvider(IMarketDataStore store, IClock clock) : IMarketDataProvider, IBarSource
{
    public DataSourceKind Kind => DataSourceKind.RecordedReplay;
    public Provenance DataProvenance => Provenance.Observed;

    public async Task<IndexQuote?> GetIndexQuoteAsync(string symbol, CancellationToken ct)
    {
        var snap = await store.GetSnapshotAsOfAsync(symbol, EasternTime.DateOf(clock.UtcNow), clock.UtcNow, ct);
        return snap?.Index is { } i && i.Symbol == symbol ? i : null;
    }

    public async Task<IReadOnlyList<OptionQuote>> GetOptionChainAsync(string underlying, DateOnly expiration, CancellationToken ct) =>
        (await store.GetSnapshotAsOfAsync(underlying, expiration, clock.UtcNow, ct))?.Options ?? [];

    public async Task<IReadOnlyList<Bar>> GetOneMinuteBarsAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var end = to < now ? to : now;
        return (await store.GetBarsAsync(symbol, Timeframe.OneMinute, from, end, ct)).Where(b => b.IsConfirmedAt(now)).ToArray();
    }
}
