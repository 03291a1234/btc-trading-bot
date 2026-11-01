namespace SpxQuant.Domain.Market;

/// <summary>Vendor Greeks. <see cref="UpdatedAt"/> matters: vendor Greeks can be much older than the bid/ask.</summary>
public sealed record OptionGreeks(
    decimal? Delta, decimal? Gamma, decimal? Theta, decimal? Vega,
    decimal? MidIv, DateTimeOffset? UpdatedAt);

/// <summary>Normalized option quote. All prices are per share; multiply by the contract multiplier for dollars.</summary>
public sealed record OptionQuote(
    string Symbol,
    string Root,
    OptionRight Right,
    DateOnly Expiration,
    decimal Strike,
    decimal? Bid,
    decimal? Ask,
    long BidSize,
    long AskSize,
    DateTimeOffset? BidAt,
    DateTimeOffset? AskAt,
    long? Volume,
    long? OpenInterest,
    OptionGreeks? Greeks,
    /// <summary>True only when trusted metadata establishes PM settlement. Root SPXW is PM-settled by product definition.</summary>
    bool IsPmSettled = false)
{
    public decimal? Spread => Bid is { } b && Ask is { } a ? a - b : null;
    public decimal? Mid => Bid is { } b && Ask is { } a ? (a + b) / 2m : null;
}

/// <summary>
/// Index quote. SPX is not traded; vendor bid/ask for the index are not meaningful
/// (observed 2026-11-01 via Tradier: bid 7524.56 / ask 7624.67 / last 7580.07), so only Last is used.
/// </summary>
public sealed record IndexQuote(string Symbol, decimal? Last, DateTimeOffset? TradeAt, DateTimeOffset ReceivedAt);

/// <summary>OHLCV bar. <see cref="Start"/> is the bar open instant; a bar is confirmed only once End &lt;= now.</summary>
public sealed record Bar(DateTimeOffset Start, TimeSpan Duration, decimal Open, decimal High, decimal Low, decimal Close, long? Volume)
{
    public DateTimeOffset End => Start + Duration;
    public bool IsConfirmedAt(DateTimeOffset now) => End <= now;
}
