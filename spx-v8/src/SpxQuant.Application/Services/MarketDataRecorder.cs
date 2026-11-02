using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;

namespace SpxQuant.Application.Services;

public sealed record RecorderOptions
{
    public bool Enabled { get; init; }
    public int ChainIntervalSeconds { get; init; } = 15;
    public int BarsIntervalSeconds { get; init; } = 60;
    /// <summary>Record this long before the open / after the close so the edges are captured.</summary>
    public int SessionPaddingMinutes { get; init; } = 2;
    public string Underlying { get; init; } = "SPX";
}

public sealed record RecordResult(string Step, bool Ok, string Detail);

/// <summary>
/// Captures OBSERVED market data for replay and research: index quote + 0DTE chain snapshots and confirmed 1-minute bars
/// (5-minute bars are derived only from complete 1-minute buckets). Never repairs or interpolates data.
/// </summary>
public sealed class MarketDataRecorder(
    IMarketDataProvider market,
    IBarSource bars,
    IMarketDataStore store,
    ITradingCalendarProvider calendar,
    IClock clock,
    MarketDataHealth health,
    RecorderOptions options)
{
    private const string VendorSource = "tradier";

    public bool InRecordingWindow(DateTimeOffset now)
    {
        var s = calendar.Calendar.SessionAt(now);
        if (!s.IsTradingDay) return false;
        var pad = TimeSpan.FromMinutes(options.SessionPaddingMinutes);
        return now >= s.Open!.Value - pad && now <= s.Close!.Value + pad;
    }

    public async Task<RecordResult> RecordChainAsync(CancellationToken ct)
    {
        var started = clock.UtcNow;
        if (!InRecordingWindow(started)) return new("chain", true, "outside session; skipped");
        var expiration = EasternTime.DateOf(started);
        try
        {
            var index = await market.GetIndexQuoteAsync(options.Underlying, ct);
            health.RecordSuccess(clock.UtcNow, Streams.Index);
            var chain = await market.GetOptionChainAsync(options.Underlying, expiration, ct);
            var capturedAt = clock.UtcNow;
            health.RecordSuccess(capturedAt, Streams.Chain);
            var info = await store.SaveChainSnapshotAsync(
                new ChainSnapshot(options.Underlying, expiration, capturedAt, VendorSource, market.DataProvenance, index, chain), ct);
            return new("chain", true, $"snapshot {info.Id}: {info.OptionCount} options");
        }
        catch (MarketDataUnavailableException ex)
        {
            health.RecordFailure(clock.UtcNow, ex.Message, Streams.Chain);
            return new("chain", false, ex.Message);
        }
    }

    /// <summary>Fetches today's 1-minute bars from the open to now and stores the confirmed ones; derives complete 5-minute bars.</summary>
    public async Task<RecordResult> RecordBarsAsync(CancellationToken ct) => await RecordBarsForDayAsync(EasternTime.DateOf(clock.UtcNow), ct);

    public async Task<RecordResult> RecordBarsForDayAsync(DateOnly day, CancellationToken ct)
    {
        var session = calendar.Calendar.SessionFor(day);
        if (!session.IsTradingDay) return new("bars", true, $"{day} is {session.Status}; skipped");
        var now = clock.UtcNow;
        if (now <= session.Open!.Value) return new("bars", true, "session not started");
        var to = now < session.Close!.Value ? now : session.Close.Value;
        try
        {
            var fetched = await bars.GetOneMinuteBarsAsync(options.Underlying, session.Open.Value, to, ct);
            var confirmed = fetched.Where(b => b.IsConfirmedAt(now) && b.Start >= session.Open.Value && b.End <= session.Close.Value)
                .OrderBy(b => b.Start).ToArray();
            var problems = BarValidator.Validate(confirmed, TimeSpan.FromMinutes(1));
            if (problems.Count > 0)
            {
                health.RecordFailure(clock.UtcNow, $"bar validation: {problems[0]}", Streams.Bars);
                return new("bars", false, $"{problems.Count} validation problems; nothing stored. First: {problems[0]}");
            }
            var new1 = await store.InsertBarsAsync(options.Underlying, Timeframe.OneMinute, confirmed, VendorSource, ct);
            var five = BarAggregator.Aggregate(confirmed, TimeSpan.FromMinutes(5), now);
            var new5 = await store.InsertBarsAsync(options.Underlying, Timeframe.FiveMinutes, five, VendorSource + "-derived", ct);
            var gaps = BarValidator.FindGaps(confirmed, session, now, TimeSpan.FromMinutes(1));
            health.RecordSuccess(clock.UtcNow, Streams.Bars);
            return new("bars", true, $"{day}: {confirmed.Length} confirmed 1m ({new1} new), {five.Count} complete 5m ({new5} new), {gaps.Count} gaps");
        }
        catch (MarketDataUnavailableException ex)
        {
            health.RecordFailure(clock.UtcNow, ex.Message, Streams.Bars);
            return new("bars", false, ex.Message);
        }
    }
}
