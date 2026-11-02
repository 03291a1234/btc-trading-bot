using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Market;
using SpxQuant.Infrastructure.Tradier;
using Xunit;

namespace SpxQuant.Infrastructure.Tests;

public class TradierJsonTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateOnly Exp = new(2026, 11, 2);

    // Shape follows Tradier's documented chain response. Values are synthetic.
    private const string Chain = """
    {"options":{"option":[
      {"symbol":"SPXW261102C07600000","root_symbol":"SPXW","option_type":"call","expiration_date":"2026-11-02","strike":7600,
       "bid":0.8,"ask":0.9,"bidsize":12,"asksize":7,"bid_date":1793631598000,"ask_date":1793631599000,"volume":321,"open_interest":4567,
       "greeks":{"delta":0.41,"gamma":0.01,"theta":-1.2,"vega":0.3,"mid_iv":0.155,"updated_at":"2026-11-02 09:59:00"}},
      {"symbol":"SPXW261102P07500000","root_symbol":"SPXW","option_type":"put","expiration_date":"2026-11-02","strike":7500,
       "bid":null,"ask":0.5,"bidsize":0,"asksize":3},
      {"symbol":"BAD","root_symbol":"SPXW","option_type":"straddle","expiration_date":"2026-11-02","strike":7500},
      {"symbol":"SPXW261103C07600000","root_symbol":"SPXW","option_type":"call","expiration_date":"2026-11-03","strike":7600,"bid":1,"ask":1.1}
    ]}}
    """;

    [Fact] public void ParsesChainAndSkipsMalformedOrMismatchedRows()
    {
        var (quotes, skipped) = TradierJson.ParseChain(Chain, Exp, Eastern);
        Assert.Equal(2, quotes.Count);
        Assert.Equal(2, skipped);
        var c = quotes[0];
        Assert.Equal(OptionRight.Call, c.Right);
        Assert.Equal(0.41m, c.Greeks!.Delta);
        Assert.Equal(12, c.BidSize);
        Assert.Equal(4567, c.OpenInterest);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793631598000), c.BidAt);
        // 09:59 Eastern on 2026-11-02 (EST) = 14:59Z
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 14, 59, 0, TimeSpan.Zero), c.Greeks.UpdatedAt!.Value.ToUniversalTime());
        var p = quotes[1];
        Assert.Null(p.Bid);              // null preserved, never coerced to 0
        Assert.Null(p.Greeks);
        Assert.Null(p.BidAt);
    }
    [Fact] public void LiveShapeGreeksTimestampIsUtcEndOfDay()
    {
        // Field layout as observed live on 2026-11-02 09:36 ET (values altered): Greeks stamped at the PRIOR session's close.
        var row = """
        {"options":{"option":[{"symbol":"SPXW261102P07490000","root_symbol":"SPXW","option_type":"put","expiration_date":"2026-11-02",
          "expiration_type":"weeklys","strike":7490.0,"bid":0.3,"ask":0.4,"bidsize":500,"asksize":450,"bid_date":1793630179000,
          "ask_date":1793630177000,"volume":1500,"open_interest":1600,"contract_size":100,
          "greeks":{"delta":-0.15,"gamma":0.002,"theta":-3.6,"vega":1.5,"rho":0.4,"phi":-0.4,"bid_iv":0.13,"mid_iv":0.14,"ask_iv":0.14,"smv_vol":0.14,"updated_at":"2026-10-30 20:00:06"}}]}}
        """;
        var q = TradierJson.ParseChain(row, Exp, TimeZoneInfo.Utc).Quotes.Single();
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 16, 0, 6, TimeSpan.FromHours(-4)), q.Greeks!.UpdatedAt); // Friday's close (EDT)
        var issues = QuoteValidator.Validate(q, DateTimeOffset.FromUnixTimeMilliseconds(1793630180000), QuoteValidationPolicy.Default, Exp);
        Assert.Contains(QuoteIssue.StaleGreeks, issues);
        Assert.Equal("UTC", new TradierOptions().GreeksTimestampZone);
    }

    [Fact] public void SingleObjectAndNullChainsHandled()
    {
        var single = """{"options":{"option":{"symbol":"S","root_symbol":"SPXW","option_type":"put","expiration_date":"2026-11-02","strike":7000,"bid":1,"ask":1.1}}}""";
        Assert.Single(TradierJson.ParseChain(single, Exp, Eastern).Quotes);
        Assert.Empty(TradierJson.ParseChain("""{"options":null}""", Exp, Eastern).Quotes);
        Assert.Empty(TradierJson.ParseChain("""{"options":{"option":null}}""", Exp, Eastern).Quotes);
    }
    [Fact] public void ParsesIndexQuoteUsingLastOnly()
    {
        var json = """{"quotes":{"quote":{"symbol":"SPX","last":7580.07,"bid":7524.56,"ask":7624.67,"trade_date":1793631599000}}}""";
        var q = TradierJson.ParseIndexQuote(json, "SPX", DateTimeOffset.UnixEpoch)!;
        Assert.Equal(7580.07m, q.Last);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793631599000), q.TradeAt);
        Assert.Null(TradierJson.ParseIndexQuote(json, "VIX", DateTimeOffset.UnixEpoch));
    }

    [Fact] public async Task ProviderIssuesGuardedGetAndMapsFailures()
    {
        var network = new RecordingHandler(r => r.RequestUri!.AbsolutePath.EndsWith("quotes")
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Chain) });
        var http = new HttpClient(new ReadOnlyMarketDataGuard { InnerHandler = network }) { BaseAddress = new Uri("https://api.tradier.com") };
        var provider = new TradierMarketDataProvider(http, new TradierOptions(), new FixedClock(DateTimeOffset.UnixEpoch), NullLogger<TradierMarketDataProvider>.Instance);

        var chain = await provider.GetOptionChainAsync("SPX", Exp, default);
        Assert.Equal(2, chain.Count);
        Assert.Equal("/v1/markets/options/chains", network.Seen[0].RequestUri!.AbsolutePath);
        Assert.Contains("greeks=true", network.Seen[0].RequestUri!.Query);
        Assert.All(network.Seen, r => Assert.Equal(HttpMethod.Get, r.Method));

        var ex = await Assert.ThrowsAsync<MarketDataUnavailableException>(() => provider.GetIndexQuoteAsync("SPX", default));
        Assert.Equal("Tradier HTTP 401", ex.Message);
    }
}
