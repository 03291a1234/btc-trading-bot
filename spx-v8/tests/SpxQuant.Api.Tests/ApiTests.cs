using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace SpxQuant.Api.Tests;

public sealed class ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ApiTests(ApiFactory factory) => _factory = factory;

    private async Task<JsonElement> Get(string path)
    {
        var r = await _factory.Authed().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact] public async Task HealthIsAnonymous()
    {
        var c = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/ready")).StatusCode);
    }

    [Theory]
    [InlineData(null)] [InlineData("wrong")] [InlineData("test-key-0123456789abcdef0123456789abcdeX")]
    public async Task ApiRequiresKey(string? key)
    {
        var c = _factory.CreateClient();
        if (key is not null) c.DefaultRequestHeaders.Add("X-Api-Key", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v8/system")).StatusCode);
    }

    [Fact] public async Task SystemReportsPaperOnlyAndSyntheticReplay()
    {
        var s = await Get("/api/v8/system");
        Assert.Equal("PAPER_ONLY", s.GetProperty("execution").GetString());
        Assert.False(s.GetProperty("liveOrderCapability").GetBoolean());
        Assert.Equal("Replay", s.GetProperty("dataSource").GetString());
        Assert.Equal("Synthetic", s.GetProperty("dataProvenance").GetString());
    }

    [Fact] public async Task RiskLimitsExposeEffectiveAndCeilings()
    {
        var r = await Get("/api/v8/risk/limits");
        Assert.Equal(100m, r.GetProperty("effective").GetProperty("maxPlannedLossPerTrade").GetDecimal());
        Assert.Equal(0.50m, r.GetProperty("ceilings").GetProperty("maxSpreadPerShare").GetDecimal());
        Assert.Equal("Estimated", r.GetProperty("feeProvenance").GetString());
    }

    [Fact] public async Task CalendarSessionUsesReplayClock()
    {
        var r = await Get("/api/v8/calendar/session");
        Assert.Equal("2026-11-02", r.GetProperty("session").GetProperty("date").GetString());
        Assert.True(r.GetProperty("entryAllowedNow").GetBoolean()); // 10:00 ET
        var holiday = await Get("/api/v8/calendar/session?date=2026-11-26");
        Assert.Equal("Closed", holiday.GetProperty("session").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.Authed().GetAsync("/api/v8/calendar/session?date=11/26/2026")).StatusCode);
    }

    [Fact] public async Task IndexQuoteIsValidated()
    {
        var r = await Get("/api/v8/market/index/SPX");
        Assert.True(r.GetProperty("valid").GetBoolean());
        Assert.Equal(7580m, r.GetProperty("quote").GetProperty("last").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.Authed().GetAsync("/api/v8/market/index/AAPL")).StatusCode);
    }

    [Fact] public async Task ChainReportsQuoteIssuesForDefectiveRows()
    {
        var r = await Get("/api/v8/options/chain/0dte?right=CALL");
        var issues = r.GetProperty("rows").EnumerateArray()
            .SelectMany(x => x.GetProperty("issues").EnumerateArray().Select(i => i.GetString())).ToHashSet();
        Assert.Contains("Crossed", issues);
        Assert.Contains("StaleBid", issues);
        Assert.Contains("ZeroBid", issues);
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.Authed().GetAsync("/api/v8/options/chain/0dte?right=STRADDLE")).StatusCode);
    }

    [Fact] public async Task SelectionRejectsSyntheticPointFourDeltaContractsAndAudits()
    {
        var c = _factory.Authed();
        var response = await c.PostAsJsonAsync("/api/v8/options/select", new { right = "CALL" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Every delta-compliant synthetic contract costs far more than $100: the system must reject, not resize.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("selected").ValueKind);
        Assert.True(body.GetProperty("rejectionCounts").GetProperty("WorstCaseLossExceeded").GetInt32() > 0);
        Assert.Equal("Synthetic", body.GetProperty("quoteProvenance").GetString());
        Assert.Equal("Estimated", body.GetProperty("riskProvenance").GetString());
        var seq = body.GetProperty("auditSequence").GetInt64();

        var audit = await Get($"/api/v8/audit?after={seq - 1}&limit=1");
        Assert.Equal("CONTRACT_SELECTION", audit[0].GetProperty("kind").GetString());
        var verify = await Get("/api/v8/audit/verify");
        Assert.True(verify.GetProperty("intact").GetBoolean());
    }

    [Fact] public async Task SelectValidatesRight() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.Authed().PostAsJsonAsync("/api/v8/options/select", new { right = "BUY" })).StatusCode);

    [Fact] public async Task NoMutatingTradingRoutesExist()
    {
        var c = _factory.Authed();
        foreach (var path in new[] { "/api/v8/orders", "/api/v8/paper/enter", "/api/orders", "/api/v8/trade" })
            Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync(path, new { })).StatusCode);
    }
}

public class StartupSafetyTests
{
    [Fact] public void LooseningRiskCeilingBlocksStartup()
    {
        using var f = ApiFactory.With(new() { ["Risk:MaxPlannedLossPerTrade"] = "500" });
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Invalid risk configuration", ex.ToString());
    }

    [Fact] public void ShortApiKeyBlocksStartup()
    {
        using var f = ApiFactory.With(new() { ["Api:Key"] = "short" });
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }

    [Fact] public void TradierModeWithoutTokenBlocksStartup()
    {
        using var f = ApiFactory.With(new() { ["Platform:DataSource"] = "TradierReadOnly" });
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Tradier:Token", ex.ToString());
    }

    [Fact] public void TradierModeRejectsNonHttpsBaseUrl()
    {
        using var f = ApiFactory.With(new() { ["Platform:DataSource"] = "TradierReadOnly", ["Tradier:Token"] = "x", ["Tradier:BaseUrl"] = "http://api.tradier.com" });
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }
}
