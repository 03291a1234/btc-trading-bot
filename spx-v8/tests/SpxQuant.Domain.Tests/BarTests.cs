using SpxQuant.Domain.Calendar;
using SpxQuant.Domain.Market;
using Xunit;

namespace SpxQuant.Domain.Tests;

public class BarTests
{
    private static readonly TimeSpan M = TimeSpan.FromMinutes(1);
    private static readonly DateTimeOffset Open = new(2026, 11, 2, 14, 30, 0, TimeSpan.Zero); // 09:30 ET

    private static List<Bar> Minutes(int n, int skip = -1) => Enumerable.Range(0, n).Where(i => i != skip)
        .Select(i => new Bar(Open.AddMinutes(i), M, 100 + i, 101 + i, 99 + i, 100.5m + i, null)).ToList();

    [Fact] public void CleanSeriesValidates() => Assert.Empty(BarValidator.Validate(Minutes(10), M));

    [Fact] public void DetectsDuplicatesMisalignmentAndBadOhlc()
    {
        var bars = Minutes(3);
        bars.Add(bars[2]);
        Assert.Contains(BarValidator.Validate(bars, M), p => p.Contains("strictly increasing"));
        Assert.Contains(BarValidator.Validate([new Bar(Open.AddSeconds(10), M, 1, 2, 0.5m, 1, null)], M), p => p.Contains("aligned"));
        Assert.Contains(BarValidator.Validate([new Bar(Open, M, 100, 99, 98, 100, null)], M), p => p.Contains("inconsistent"));
        Assert.Contains(BarValidator.Validate([new Bar(Open, M, 0, 1, 0, 1, null)], M), p => p.Contains("non-positive"));
    }

    [Fact] public void FiveMinuteBarsOnlyFromCompleteElapsedBuckets()
    {
        var now = Open.AddMinutes(12);
        var five = BarAggregator.Aggregate(Minutes(12), TimeSpan.FromMinutes(5), now);
        Assert.Equal([Open, Open.AddMinutes(5)], five.Select(b => b.Start));
        var first = five[0];
        Assert.Equal(100m, first.Open);
        Assert.Equal(105m, first.High);    // max of 101..105
        Assert.Equal(99m, first.Low);
        Assert.Equal(104.5m, first.Close); // close of 4th minute
        Assert.Null(first.Volume);
    }

    [Fact] public void MissingMinuteSuppressesItsBucket() =>
        Assert.Equal([Open], BarAggregator.Aggregate(Minutes(10, skip: 7), TimeSpan.FromMinutes(5), Open.AddMinutes(10)).Select(b => b.Start));

    [Fact] public void FormingMinuteIsNeverAggregated()
    {
        // At 09:34:30 the 09:34 minute is still forming, so the 09:30 bucket is not complete.
        Assert.Empty(BarAggregator.Aggregate(Minutes(5), TimeSpan.FromMinutes(5), Open.AddMinutes(4).AddSeconds(30)));
        Assert.Single(BarAggregator.Aggregate(Minutes(5), TimeSpan.FromMinutes(5), Open.AddMinutes(5)));
    }

    [Fact] public void GapsAreFullyElapsedMissingMinutesOnly()
    {
        var cal = new TradingCalendar(new(2026, 1, 1), new(2026, 12, 31), [], "t");
        var session = cal.SessionFor(new(2026, 11, 2));
        var gaps = BarValidator.FindGaps(Minutes(6, skip: 3), session, Open.AddMinutes(6).AddSeconds(30), M);
        Assert.Equal([Open.AddMinutes(3)], gaps);
        Assert.Empty(BarValidator.FindGaps([], cal.SessionFor(new(2026, 11, 1)), Open, M)); // Sunday
    }
}
