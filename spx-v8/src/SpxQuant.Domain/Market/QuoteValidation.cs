namespace SpxQuant.Domain.Market;

public enum QuoteIssue
{
    MissingBid,
    MissingAsk,
    NegativeBid,
    ZeroBid,
    NonPositiveAsk,
    Crossed,
    MissingBidTimestamp,
    MissingAskTimestamp,
    StaleBid,
    StaleAsk,
    FutureTimestamp,
    ZeroBidSize,
    ZeroAskSize,
    MissingGreeks,
    StaleGreeks,
    InvalidDelta,
    Expired,
    InvalidStrike,
}

public sealed record QuoteValidationPolicy(
    TimeSpan MaxQuoteAge,
    TimeSpan MaxGreeksAge,
    /// <summary>Tolerated clock skew before a vendor timestamp counts as "in the future".</summary>
    TimeSpan FutureTolerance)
{
    public static QuoteValidationPolicy Default { get; } =
        new(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));
}

public static class QuoteValidator
{
    /// <summary>Returns every problem found. An empty set means the quote is usable for entry evaluation.</summary>
    public static IReadOnlySet<QuoteIssue> Validate(OptionQuote q, DateTimeOffset now, QuoteValidationPolicy policy, DateOnly todayEastern)
    {
        var issues = new HashSet<QuoteIssue>();
        if (q.Strike <= 0) issues.Add(QuoteIssue.InvalidStrike);
        if (q.Expiration < todayEastern) issues.Add(QuoteIssue.Expired);

        if (q.Bid is null) issues.Add(QuoteIssue.MissingBid);
        else if (q.Bid < 0) issues.Add(QuoteIssue.NegativeBid);
        else if (q.Bid == 0) issues.Add(QuoteIssue.ZeroBid);

        if (q.Ask is null) issues.Add(QuoteIssue.MissingAsk);
        else if (q.Ask <= 0) issues.Add(QuoteIssue.NonPositiveAsk);

        if (q.Bid is { } b && q.Ask is { } a && b > a) issues.Add(QuoteIssue.Crossed);
        if (q.BidSize <= 0) issues.Add(QuoteIssue.ZeroBidSize);
        if (q.AskSize <= 0) issues.Add(QuoteIssue.ZeroAskSize);

        CheckStamp(q.BidAt, QuoteIssue.MissingBidTimestamp, QuoteIssue.StaleBid, policy.MaxQuoteAge);
        CheckStamp(q.AskAt, QuoteIssue.MissingAskTimestamp, QuoteIssue.StaleAsk, policy.MaxQuoteAge);

        if (q.Greeks?.Delta is not { } delta) issues.Add(QuoteIssue.MissingGreeks);
        else
        {
            var signOk = q.Right == OptionRight.Call ? delta > 0 && delta <= 1 : delta < 0 && delta >= -1;
            if (!signOk) issues.Add(QuoteIssue.InvalidDelta);
            // Greeks without a timestamp cannot be proven fresh.
            if (q.Greeks.UpdatedAt is not { } g || now - g > policy.MaxGreeksAge) issues.Add(QuoteIssue.StaleGreeks);
            else if (g - now > policy.FutureTolerance) issues.Add(QuoteIssue.FutureTimestamp);
        }
        return issues;

        void CheckStamp(DateTimeOffset? stamp, QuoteIssue missing, QuoteIssue stale, TimeSpan maxAge)
        {
            if (stamp is not { } at) { issues.Add(missing); return; }
            if (at - now > policy.FutureTolerance) { issues.Add(QuoteIssue.FutureTimestamp); return; }
            if (now - at > maxAge) issues.Add(stale);
        }
    }

    public static IReadOnlyList<string> ValidateIndex(IndexQuote q, DateTimeOffset now, TimeSpan maxAge)
    {
        var problems = new List<string>();
        if (q.Last is not > 0) problems.Add("MissingOrNonPositiveLast");
        if (q.TradeAt is not { } at) problems.Add("MissingTimestamp");
        else if (at - now > TimeSpan.FromSeconds(2)) problems.Add("FutureTimestamp");
        else if (now - at > maxAge) problems.Add("Stale");
        return problems;
    }
}
