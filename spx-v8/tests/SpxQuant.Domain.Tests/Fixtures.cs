using SpxQuant.Domain.Market;

namespace SpxQuant.Domain.Tests;

internal static class Fixtures
{
    // 2026-11-02 10:00 ET (EST, UTC-5) — a regular session Monday.
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Today = new(2026, 11, 2);

    public static OptionQuote Quote(decimal strike = 7600, OptionRight right = OptionRight.Call, decimal? bid = 0.80m, decimal? ask = 0.90m,
        decimal? delta = 0.40m, string root = "SPXW", DateOnly? expiration = null, DateTimeOffset? at = null, DateTimeOffset? greeksAt = null,
        long bidSize = 10, long askSize = 10, long? volume = 100, long? oi = 500, string? symbol = null) =>
        new(symbol ?? $"{root}261102{(right == OptionRight.Call ? 'C' : 'P')}{(int)(strike * 1000):D8}", root, right,
            expiration ?? Today, strike, bid, ask, bidSize, askSize, at ?? Now.AddSeconds(-2), at ?? Now.AddSeconds(-2), volume, oi,
            delta is null ? null : new OptionGreeks(delta, 0.01m, -1m, 0.1m, 0.16m, greeksAt ?? Now.AddSeconds(-60)));
}
