using SpxQuant.Domain.Calendar;

namespace SpxQuant.Domain.Market;

public enum Timeframe { OneMinute, FiveMinutes }

public static class TimeframeExtensions
{
    public static TimeSpan Duration(this Timeframe tf) => tf switch
    {
        Timeframe.OneMinute => TimeSpan.FromMinutes(1),
        Timeframe.FiveMinutes => TimeSpan.FromMinutes(5),
        _ => throw new ArgumentOutOfRangeException(nameof(tf)),
    };
    public static string Code(this Timeframe tf) => tf == Timeframe.OneMinute ? "1m" : "5m";
    public static bool TryParse(string? code, out Timeframe tf)
    {
        switch (code) { case "1m": tf = Timeframe.OneMinute; return true; case "5m": tf = Timeframe.FiveMinutes; return true; }
        tf = default; return false;
    }
}

public static class BarValidator
{
    /// <summary>Every contract violation in a bar series; data is never repaired silently.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<Bar> bars, TimeSpan duration)
    {
        var problems = new List<string>();
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            if (b.Duration != duration) problems.Add($"{b.Start:O}: duration {b.Duration} != {duration}");
            if (b.Start.ToUnixTimeMilliseconds() % (long)duration.TotalMilliseconds != 0) problems.Add($"{b.Start:O}: not aligned to {duration}");
            if (b.Open <= 0 || b.High <= 0 || b.Low <= 0 || b.Close <= 0) problems.Add($"{b.Start:O}: non-positive price");
            if (b.High < Math.Max(b.Open, b.Close) || b.Low > Math.Min(b.Open, b.Close) || b.High < b.Low) problems.Add($"{b.Start:O}: inconsistent OHLC");
            if (b.Volume < 0) problems.Add($"{b.Start:O}: negative volume");
            if (i > 0 && b.Start <= bars[i - 1].Start) problems.Add($"{b.Start:O}: not strictly increasing (duplicate or out of order)");
        }
        return problems;
    }

    /// <summary>Expected bar starts in [session open, min(close, now)) that are missing. Only fully elapsed bars are expected.</summary>
    public static IReadOnlyList<DateTimeOffset> FindGaps(IReadOnlyList<Bar> bars, TradingSession session, DateTimeOffset now, TimeSpan duration)
    {
        if (!session.IsTradingDay) return [];
        var have = bars.Select(b => b.Start.ToUniversalTime()).ToHashSet();
        var end = now < session.Close!.Value ? now : session.Close.Value;
        var gaps = new List<DateTimeOffset>();
        for (var t = session.Open!.Value.ToUniversalTime(); t + duration <= end; t += duration)
            if (!have.Contains(t)) gaps.Add(t);
        return gaps;
    }
}

public static class BarAggregator
{
    /// <summary>
    /// Builds higher-timeframe bars from 1-minute bars. A bucket is emitted only when every constituent minute is present
    /// and the bucket has fully elapsed at <paramref name="now"/>, so partial bars can never reach regime logic.
    /// </summary>
    public static IReadOnlyList<Bar> Aggregate(IEnumerable<Bar> oneMinute, TimeSpan target, DateTimeOffset now)
    {
        var minute = TimeSpan.FromMinutes(1);
        if (target <= minute || target.Ticks % minute.Ticks != 0) throw new ArgumentException("Target must be a multiple of one minute");
        var per = (int)(target / minute);
        var targetMs = (long)target.TotalMilliseconds;
        return oneMinute
            .Where(b => b.Duration == minute && b.IsConfirmedAt(now))
            .GroupBy(b => DateTimeOffset.FromUnixTimeMilliseconds(b.Start.ToUnixTimeMilliseconds() / targetMs * targetMs))
            .Where(g => g.Select(b => b.Start).Distinct().Count() == per && g.Key + target <= now)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(b => b.Start).ToArray();
                long? volume = ordered.All(b => b.Volume is null) ? null : ordered.Sum(b => b.Volume ?? 0);
                return new Bar(g.Key, target, ordered[0].Open, ordered.Max(b => b.High), ordered.Min(b => b.Low), ordered[^1].Close, volume);
            })
            .ToArray();
    }
}
