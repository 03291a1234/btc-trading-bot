using SpxQuant.Domain.Market;
using Xunit;
using static SpxQuant.Domain.Tests.Fixtures;

namespace SpxQuant.Domain.Tests;

public class QuoteValidatorTests
{
    private static IReadOnlySet<QuoteIssue> V(OptionQuote q) => QuoteValidator.Validate(q, Now, QuoteValidationPolicy.Default, Today);

    [Fact] public void CleanQuoteHasNoIssues() => Assert.Empty(V(Quote()));
    [Fact] public void CrossedQuoteDetected() => Assert.Contains(QuoteIssue.Crossed, V(Quote(bid: 1.0m, ask: 0.9m)));
    [Fact] public void LockedQuoteIsNotCrossed() => Assert.DoesNotContain(QuoteIssue.Crossed, V(Quote(bid: 0.9m, ask: 0.9m)));
    [Fact] public void MissingSidesDetected()
    {
        var issues = V(Quote(bid: null, ask: null));
        Assert.Contains(QuoteIssue.MissingBid, issues);
        Assert.Contains(QuoteIssue.MissingAsk, issues);
    }
    [Fact] public void ZeroBidAndNonPositiveAskDetected()
    {
        Assert.Contains(QuoteIssue.ZeroBid, V(Quote(bid: 0m)));
        Assert.Contains(QuoteIssue.NonPositiveAsk, V(Quote(bid: 0m, ask: 0m)));
        Assert.Contains(QuoteIssue.NegativeBid, V(Quote(bid: -0.1m)));
    }
    [Fact] public void StaleQuoteDetected() => Assert.Contains(QuoteIssue.StaleBid, V(Quote(at: Now.AddSeconds(-16))));
    [Fact] public void QuoteAtExactMaxAgeIsFresh() => Assert.DoesNotContain(QuoteIssue.StaleBid, V(Quote(at: Now.AddSeconds(-15))));
    [Fact] public void FutureTimestampDetected() => Assert.Contains(QuoteIssue.FutureTimestamp, V(Quote(at: Now.AddSeconds(5))));
    [Fact] public void SmallClockSkewTolerated() => Assert.DoesNotContain(QuoteIssue.FutureTimestamp, V(Quote(at: Now.AddSeconds(1))));
    [Fact] public void MissingTimestampsDetected()
    {
        var q = Quote() with { BidAt = null, AskAt = null };
        var issues = V(q);
        Assert.Contains(QuoteIssue.MissingBidTimestamp, issues);
        Assert.Contains(QuoteIssue.MissingAskTimestamp, issues);
    }
    [Fact] public void MissingAndStaleGreeksDetected()
    {
        Assert.Contains(QuoteIssue.MissingGreeks, V(Quote(delta: null)));
        Assert.Contains(QuoteIssue.StaleGreeks, V(Quote(greeksAt: Now.AddMinutes(-6))));
        var noStamp = Quote() with { Greeks = new OptionGreeks(0.4m, null, null, null, null, null) };
        Assert.Contains(QuoteIssue.StaleGreeks, V(noStamp));
    }
    [Fact] public void DeltaSignMustMatchRight()
    {
        Assert.Contains(QuoteIssue.InvalidDelta, V(Quote(right: OptionRight.Put, delta: 0.4m)));
        Assert.Contains(QuoteIssue.InvalidDelta, V(Quote(delta: 1.2m)));
        Assert.Empty(V(Quote(right: OptionRight.Put, delta: -0.4m)));
    }
    [Fact] public void ZeroSizesAndExpiryDetected()
    {
        Assert.Contains(QuoteIssue.ZeroBidSize, V(Quote(bidSize: 0)));
        Assert.Contains(QuoteIssue.ZeroAskSize, V(Quote(askSize: 0)));
        Assert.Contains(QuoteIssue.Expired, V(Quote(expiration: Today.AddDays(-1))));
    }
    [Fact] public void IndexQuoteValidation()
    {
        Assert.Empty(QuoteValidator.ValidateIndex(new IndexQuote("SPX", 7580m, Now.AddSeconds(-1), Now), Now, TimeSpan.FromSeconds(15)));
        Assert.Contains("Stale", QuoteValidator.ValidateIndex(new IndexQuote("SPX", 7580m, Now.AddMinutes(-1), Now), Now, TimeSpan.FromSeconds(15)));
        Assert.Contains("MissingOrNonPositiveLast", QuoteValidator.ValidateIndex(new IndexQuote("SPX", null, Now, Now), Now, TimeSpan.FromSeconds(15)));
    }
}
