using SpxQuant.Application.Services;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;
using SpxQuant.Domain.Options;
using SpxQuant.Domain.Risk;
using Xunit;

namespace SpxQuant.Application.Tests;

public class GreeksPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);
    private readonly FakeMarket _market = new() { Index = new IndexQuote("SPX", 7580m, Now, Now) };
    private readonly MemoryAudit _audit = new();

    private ContractSelectionService Service(GreeksSource source) => new(_market, new FakeCalendar(), new MutableClock(Now), _audit,
        new MarketDataHealth(), new RiskLimits(), QuoteValidationPolicy.Default, new GreeksOptions(source));

    // Far OTM call with a cheap premium; vendor Greeks are an hour old.
    private static OptionQuote Stale(decimal bid, decimal ask, decimal strike) => new($"SPXW261102C{strike * 1000:00000000}", "SPXW", OptionRight.Call,
        new(2026, 11, 2), strike, bid, ask, 10, 10, Now.AddSeconds(-2), Now.AddSeconds(-2), 100, 500,
        new OptionGreeks(0.40m, null, null, null, null, Now.AddHours(-1)));

    [Fact] public async Task VendorPolicyRejectsStaleGreeks()
    {
        _market.Chain = [Stale(0.85m, 0.95m, 7600)];
        var r = await Service(GreeksSource.Vendor).EvaluateAsync(OptionRight.Call, default);
        Assert.Equal(1, r.Selection.RejectionCounts[RejectionReason.StaleGreeks]);
        Assert.Equal(0, r.EstimatedGreeksCount);
    }

    [Fact] public async Task LocalPolicyReplacesGreeksWithLabeledEstimates()
    {
        _market.Chain = [Stale(0.85m, 0.95m, 7600)];
        var r = await Service(GreeksSource.LocalEstimated).EvaluateAsync(OptionRight.Call, default);
        Assert.Equal(1, r.EstimatedGreeksCount);
        Assert.DoesNotContain(RejectionReason.StaleGreeks, r.Selection.RejectionCounts.Where(kv => kv.Value > 0).Select(kv => kv.Key));
        Assert.Contains(_audit.Records, a => a.PayloadJson.Contains("\"estimatedGreeks\":1"));
    }

    [Fact] public async Task LocalPolicyNeverFallsBackToStaleVendorGreeks()
    {
        _market.Index = null; // no spot -> cannot estimate
        _market.Chain = [Stale(0.85m, 0.95m, 7600)];
        var r = await Service(GreeksSource.LocalEstimated).EvaluateAsync(OptionRight.Call, default);
        Assert.Null(r.Selection.Selected);
        Assert.Equal(1, r.Selection.RejectionCounts[RejectionReason.DeltaOutOfRange]);
    }

    [Fact] public async Task VendorThenLocalKeepsFreshVendorGreeks()
    {
        var fresh = Stale(0.85m, 0.95m, 7600) with { Greeks = new OptionGreeks(0.40m, null, null, null, null, Now.AddSeconds(-30)) };
        _market.Chain = [fresh];
        var r = await Service(GreeksSource.VendorThenLocal).EvaluateAsync(OptionRight.Call, default);
        Assert.Equal(0, r.EstimatedGreeksCount);
        Assert.Equal(Provenance.Observed, r.Selection.Selected!.Quote.GreeksProvenance);
    }
}
