using SpxQuant.Application.Services;
using SpxQuant.Domain.Market;
using Xunit;

namespace SpxQuant.Application.Tests;

public class RecorderTests
{
    private static readonly DateTimeOffset Open = new(2026, 11, 2, 14, 30, 0, TimeSpan.Zero); // 09:30 ET
    private readonly FakeMarket _market = new();
    private readonly MemoryStore _store = new();
    private readonly MarketDataHealth _health = new();
    private readonly MutableClock _clock = new(Open.AddMinutes(12).AddSeconds(30));

    private MarketDataRecorder Recorder() => new(_market, _market, _store, new FakeCalendar(), _clock, _health, new RecorderOptions { Enabled = true });

    private static List<Bar> Minutes(int n) => Enumerable.Range(0, n)
        .Select(i => new Bar(Open.AddMinutes(i), TimeSpan.FromMinutes(1), 7580 + i, 7581 + i, 7579 + i, 7580.5m + i, 0)).ToList();

    [Fact] public async Task StoresOnlyConfirmedBarsAndCompleteFiveMinuteBars()
    {
        _market.Bars = Minutes(13); // the 09:42 bar is still forming at 09:42:30
        var r = await Recorder().RecordBarsAsync(default);
        Assert.True(r.Ok, r.Detail);
        Assert.Equal(12, _store.Bars.Keys.Count(k => k.Item2 == Timeframe.OneMinute));
        Assert.Equal(2, _store.Bars.Keys.Count(k => k.Item2 == Timeframe.FiveMinutes));
        Assert.Equal("HEALTHY", _health.StreamSnapshot(Streams.Bars, _clock.UtcNow, TimeSpan.FromMinutes(2)).Status);
        // Second run is idempotent.
        var again = await Recorder().RecordBarsAsync(default);
        Assert.Contains("(0 new)", again.Detail);
    }

    [Fact] public async Task InvalidBarsAreNotStoredAtAll()
    {
        var bars = Minutes(5);
        bars[2] = bars[2] with { High = 1m };
        _market.Bars = bars;
        var r = await Recorder().RecordBarsAsync(default);
        Assert.False(r.Ok);
        Assert.Empty(_store.Bars);
        Assert.Equal("UNHEALTHY", _health.StreamSnapshot(Streams.Bars, _clock.UtcNow, TimeSpan.FromMinutes(2)).Status);
    }

    [Fact] public async Task ReportsGaps()
    {
        _market.Bars = Minutes(12).Where((_, i) => i != 4).ToList();
        var r = await Recorder().RecordBarsAsync(default);
        Assert.Contains("1 gaps", r.Detail);
        Assert.Equal(1, _store.Bars.Keys.Count(k => k.Item2 == Timeframe.FiveMinutes)); // 09:30 bucket incomplete
    }

    [Fact] public async Task ChainSnapshotIsStampedWithArrivalTime()
    {
        _market.Index = new IndexQuote("SPX", 7580m, Open, Open);
        _market.Chain = [new OptionQuote("X", "SPXW", OptionRight.Call, new(2026, 11, 2), 7600, 1, 1.1m, 1, 1, Open, Open, 1, 1, null)];
        var r = await Recorder().RecordChainAsync(default);
        Assert.True(r.Ok);
        var snap = Assert.Single(_store.Snapshots);
        Assert.Equal(_clock.UtcNow, snap.CapturedAt);
        Assert.Equal(new DateOnly(2026, 11, 2), snap.Expiration);
        Assert.Equal(Domain.Common.Provenance.Observed, snap.Provenance);
    }

    [Fact] public async Task SkipsOutsideSessionAndOnHolidays()
    {
        _clock.UtcNow = new DateTimeOffset(2026, 11, 26, 16, 0, 0, TimeSpan.Zero); // Thanksgiving
        Assert.Contains("outside session", (await Recorder().RecordChainAsync(default)).Detail);
        Assert.Contains("Closed", (await Recorder().RecordBarsAsync(default)).Detail);
        _clock.UtcNow = Open.AddHours(-3);
        Assert.False(Recorder().InRecordingWindow(_clock.UtcNow));
        Assert.Empty(_store.Snapshots);
    }

    [Fact] public async Task VendorFailureIsReportedNotThrown()
    {
        _market.Fail = true;
        var r = await Recorder().RecordChainAsync(default);
        Assert.False(r.Ok);
        Assert.True(_health.IsStale(Streams.Chain, _clock.UtcNow, TimeSpan.FromMinutes(2)));
    }
}
