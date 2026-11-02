using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;
using SpxQuant.Domain.Pricing;
using Xunit;
using static SpxQuant.Domain.Tests.Fixtures;

namespace SpxQuant.Domain.Tests;

public class BlackScholesTests
{
    [Fact] public void MatchesTextbookValue()
    {
        // Hull: S=42, K=40, r=10%, sigma=20%, T=0.5 -> call 4.76, put 0.81
        Assert.Equal(4.76, BlackScholes.Price(42, 40, 0.5, 0.2, 0.1, OptionRight.Call), 2);
        Assert.Equal(0.81, BlackScholes.Price(42, 40, 0.5, 0.2, 0.1, OptionRight.Put), 2);
    }

    [Theory]
    [InlineData(7580, 7600, 6.0)] [InlineData(7580, 7500, 1.0)] [InlineData(7580, 7700, 0.25)]
    public void PutCallParityHolds(double s, double k, double hours)
    {
        var t = hours / (365 * 24);
        var c = BlackScholes.Price(s, k, t, 0.16, 0.04, OptionRight.Call);
        var p = BlackScholes.Price(s, k, t, 0.16, 0.04, OptionRight.Put);
        Assert.Equal(s - k * Math.Exp(-0.04 * t), c - p, 6);
    }

    [Fact] public void ImpliedVolRoundTrips()
    {
        var t = 4.0 / (365 * 24);
        var price = BlackScholes.Price(7580, 7600, t, 0.18, 0.04, OptionRight.Call);
        Assert.Equal(0.18, BlackScholes.ImpliedVol(price, 7580, 7600, t, 0.04, OptionRight.Call)!.Value, 5);
    }

    [Fact] public void ImpliedVolRejectsArbitragePrices()
    {
        var t = 4.0 / (365 * 24);
        Assert.Null(BlackScholes.ImpliedVol(10, 7580, 7500, t, 0.04, OptionRight.Call)); // below intrinsic 80
        Assert.Null(BlackScholes.ImpliedVol(0, 7580, 7600, t, 0.04, OptionRight.Call));
    }

    [Fact] public void EstimatedGreeksAreLabeledAndSigned()
    {
        var close = new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero);
        var call = Quote(7600, bid: 12.4m, ask: 12.9m, delta: null);
        var est = BlackScholes.WithEstimatedGreeks(call, 7580m, Now, close, 0.04)!;
        Assert.Equal(Provenance.Estimated, est.GreeksProvenance);
        Assert.InRange(est.Greeks!.Delta!.Value, 0.3m, 0.6m);
        Assert.Equal(call.BidAt, est.Greeks.UpdatedAt); // as fresh as the quote it came from, no fresher
        var put = BlackScholes.WithEstimatedGreeks(Quote(7560, OptionRight.Put, bid: 5m, ask: 5.1m, delta: null), 7580m, Now, close, 0.04)!;
        Assert.True(put.Greeks!.Delta < 0);
    }

    [Fact] public void NoEstimateWithoutUsableInputs()
    {
        var close = new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero);
        Assert.Null(BlackScholes.WithEstimatedGreeks(Quote(bid: 0m), 7580m, Now, close, 0.04));
        Assert.Null(BlackScholes.WithEstimatedGreeks(Quote() with { BidAt = null }, 7580m, Now, close, 0.04));
        Assert.Null(BlackScholes.WithEstimatedGreeks(Quote(), 7580m, close, close, 0.04)); // expired
    }
}
