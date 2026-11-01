using SpxQuant.Domain.Market;
using SpxQuant.Domain.Options;
using SpxQuant.Domain.Risk;
using Xunit;
using static SpxQuant.Domain.Tests.Fixtures;

namespace SpxQuant.Domain.Tests;

public class ContractSelectorTests
{
    private static ContractSelector Selector(RiskLimits? l = null) => new(l ?? new RiskLimits(), QuoteValidationPolicy.Default);
    private static IReadOnlyList<RejectionReason> Reasons(OptionQuote q, OptionRight r = OptionRight.Call) =>
        Selector().Evaluate(q, r, Today, Now).Rejections;

    [Fact] public void SelectsClosestDeltaAmongEligible()
    {
        var result = Selector().Select([Quote(7600, delta: 0.30m), Quote(7595, delta: 0.41m), Quote(7590, delta: 0.47m)], OptionRight.Call, Now);
        Assert.Equal(7595, result.Selected!.Quote.Strike);
        Assert.Equal(3, result.Ranked.Count);
    }
    [Fact] public void TieBreaksOnSpreadThenSizeDeterministically()
    {
        var a = Quote(7600, bid: 0.80m, ask: 0.90m, symbol: "A");
        var b = Quote(7605, bid: 0.85m, ask: 0.90m, symbol: "B");
        Assert.Equal("B", Selector().Select([a, b], OptionRight.Call, Now).Selected!.Quote.Symbol);
        Assert.Equal("B", Selector().Select([b, a], OptionRight.Call, Now).Selected!.Quote.Symbol);
    }
    [Fact] public void RejectsWrongExpirationAndAmSettledRoot()
    {
        Assert.Contains(RejectionReason.WrongExpiration, Reasons(Quote(expiration: Today.AddDays(1))));
        Assert.Contains(RejectionReason.NotPmSettled, Reasons(Quote(root: "SPX")));
        var pmSpx = Quote(root: "SPX") with { IsPmSettled = true };
        Assert.DoesNotContain(RejectionReason.NotPmSettled, Reasons(pmSpx));
    }
    [Fact] public void RejectsWrongRightAndDeltaOutsideTolerance()
    {
        Assert.Contains(RejectionReason.WrongRight, Reasons(Quote(right: OptionRight.Put, delta: -0.4m)));
        Assert.Contains(RejectionReason.DeltaOutOfRange, Reasons(Quote(delta: 0.24m)));
        Assert.Contains(RejectionReason.DeltaOutOfRange, Reasons(Quote(delta: 0.56m)));
        Assert.Empty(Reasons(Quote(delta: 0.55m)));
        Assert.Contains(RejectionReason.DeltaOutOfRange, Reasons(Quote(delta: null)));
    }
    [Fact] public void SpreadLimitIsInclusiveAtFiftyCents()
    {
        Assert.DoesNotContain(RejectionReason.SpreadTooWide, Reasons(Quote(bid: 0.40m, ask: 0.90m)));
        Assert.Contains(RejectionReason.SpreadTooWide, Reasons(Quote(bid: 0.39m, ask: 0.90m)));
    }
    [Fact] public void RejectsContractsWhoseBidIsAlreadyAtTheStop()
    {
        // ask 0.90, 30% stop -> trigger 0.63. v7 would buy at 0.90 and stop on the next tick at a 0.60 bid.
        Assert.Contains(RejectionReason.StopInsideSpread, Reasons(Quote(bid: 0.60m, ask: 0.90m)));
        Assert.Contains(RejectionReason.StopInsideSpread, Reasons(Quote(bid: 0.63m, ask: 0.90m)));
        Assert.DoesNotContain(RejectionReason.StopInsideSpread, Reasons(Quote(bid: 0.64m, ask: 0.90m)));
    }
    [Fact] public void WorstCaseLossCapIncludesBothFees()
    {
        // ask*100 + $2 fees: 0.98 -> $100 (allowed), 0.99 -> $101 (rejected)
        Assert.Empty(Reasons(Quote(bid: 0.90m, ask: 0.98m)));
        var r = Reasons(Quote(bid: 0.90m, ask: 0.99m));
        Assert.Contains(RejectionReason.WorstCaseLossExceeded, r);
        Assert.Contains(RejectionReason.PremiumExposureExceeded, Reasons(Quote(bid: 0.95m, ask: 1.00m)));
    }
    [Fact] public void TypicalPointFourDeltaSpxwIsRejectedNotResized()
    {
        // A realistic 0DTE 0.40-delta SPXW premium (~$8) violates the $100 caps; the selector must reject, not loosen.
        var result = Selector().Select([Quote(bid: 7.90m, ask: 8.10m)], OptionRight.Call, Now);
        Assert.Null(result.Selected);
        var e = Selector().Evaluate(Quote(bid: 7.90m, ask: 8.10m), OptionRight.Call, Today, Now);
        Assert.Contains(RejectionReason.WorstCaseLossExceeded, e.Rejections);
        Assert.Contains(RejectionReason.PlannedLossExceeded, e.Rejections);
        Assert.Equal(812m, e.Risk!.WorstCaseLoss);
    }
    [Fact] public void AggregatePremiumAccountsForExistingExposure() =>
        Assert.Contains(RejectionReason.PremiumExposureExceeded, Selector().Evaluate(Quote(bid: 0.45m, ask: 0.50m), OptionRight.Call, Today, Now, currentAggregatePremium: 60m).Rejections);
    [Fact] public void LiquidityRequiresSizeOpenInterestAndVolume()
    {
        Assert.Contains(RejectionReason.InsufficientLiquidity, Reasons(Quote(bidSize: 0)));
        Assert.Contains(RejectionReason.InsufficientLiquidity, Reasons(Quote(oi: 5)));
        Assert.Contains(RejectionReason.InsufficientLiquidity, Reasons(Quote(oi: null)));
        Assert.Contains(RejectionReason.InsufficientLiquidity, Reasons(Quote(volume: 0)));
    }
    [Fact] public void StaleQuotesAndStaleGreeksAreSeparateReasons()
    {
        Assert.Contains(RejectionReason.StaleQuote, Reasons(Quote(at: Now.AddSeconds(-30))));
        var g = Reasons(Quote(greeksAt: Now.AddHours(-1)));
        Assert.Contains(RejectionReason.StaleGreeks, g);
        Assert.DoesNotContain(RejectionReason.StaleQuote, g);
    }
    [Fact] public void CrossedQuoteIsInvalid() => Assert.Contains(RejectionReason.InvalidQuote, Reasons(Quote(bid: 1.0m, ask: 0.9m)));
    [Fact] public void RejectionCountsAreIndependentNotSequential()
    {
        // Both wrong expiration AND wide spread: counted under each reason.
        var result = Selector().Select([Quote(bid: 0.10m, ask: 0.90m, expiration: Today.AddDays(1))], OptionRight.Call, Now);
        Assert.Equal(1, result.RejectionCounts[RejectionReason.StopInsideSpread]);
        Assert.Equal(1, result.RejectionCounts[RejectionReason.WrongExpiration]);
        Assert.Equal(1, result.RejectionCounts[RejectionReason.SpreadTooWide]);
    }
    [Fact] public void EmptyChainYieldsNoSelection()
    {
        var result = Selector().Select([], OptionRight.Put, Now);
        Assert.Null(result.Selected);
        Assert.Equal(0, result.Received);
    }
    [Fact] public void TighterConfiguredLimitsApply() =>
        Assert.Contains(RejectionReason.SpreadTooWide, Selector(new RiskLimits { MaxSpreadPerShare = 0.05m }).Evaluate(Quote(), OptionRight.Call, Today, Now).Rejections);
}
