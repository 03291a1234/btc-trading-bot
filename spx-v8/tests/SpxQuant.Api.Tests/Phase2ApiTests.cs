using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;
using SpxQuant.Infrastructure.Persistence;
using Xunit;

namespace SpxQuant.Api.Tests;

public sealed class Phase2ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public Phase2ApiTests(ApiFactory factory) => _factory = factory;

    [Fact] public async Task MarketHealthListsStreamsAndRecorderState()
    {
        var c = _factory.Authed();
        await c.PostAsJsonAsync("/api/v8/options/select", new { right = "PUT" });
        var h = await c.GetFromJsonAsync<JsonElement>("/api/v8/market/health");
        Assert.Equal("HEALTHY", h.GetProperty("streams").GetProperty("chain").GetProperty("status").GetString());
        Assert.False(h.GetProperty("recorder").GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("GET", "/api/v8/market/bars/SPX")]
    [InlineData("GET", "/api/v8/market/snapshots")]
    [InlineData("POST", "/api/v8/recorder/run?step=chain")]
    [InlineData("GET", "/api/v8/calendar/crosscheck?year=2026&month=11")]
    public async Task StorageAndBrokerRoutesReport503WhenNotConfigured(string method, string path)
    {
        var r = await _factory.Authed().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    [Fact] public async Task SelectionReportsGreeksSource()
    {
        var r = await (await _factory.Authed().PostAsJsonAsync("/api/v8/options/select", new { right = "CALL" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Vendor", r.GetProperty("greeksSource").GetString());
    }
}

public class Phase2StartupTests
{
    [Fact] public void RecorderRequiresPostgresAndTradier()
    {
        using var f = ApiFactory.With(new() { ["Recorder:Enabled"] = "true" });
        Assert.Contains("Recorder:Enabled requires", Assert.ThrowsAny<Exception>(() => f.CreateClient()).ToString());
    }

    [Fact] public void RecordedReplayRequiresPostgres()
    {
        using var f = ApiFactory.With(new() { ["Platform:DataSource"] = "RecordedReplay" });
        Assert.Contains("RecordedReplay requires", Assert.ThrowsAny<Exception>(() => f.CreateClient()).ToString());
    }

    [Fact] public void RecorderIntervalsAreBounded()
    {
        using var f = ApiFactory.With(new() { ["Recorder:ChainIntervalSeconds"] = "1" });
        Assert.Contains("Recorder intervals", Assert.ThrowsAny<Exception>(() => f.CreateClient()).ToString());
    }
}

/// <summary>End-to-end recorded replay through the HTTP API against a real PostgreSQL (SPXQ_TEST_PG).</summary>
public sealed class RecordedReplayApiTests : IAsyncLifetime
{
    private static readonly string? Cs = Environment.GetEnvironmentVariable("SPXQ_TEST_PG");
    private readonly string _schema = "api_" + Guid.NewGuid().ToString("N")[..12];
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero); // 10:00 ET

    public async Task InitializeAsync()
    {
        if (Cs is null) return;
        await using var admin = NpgsqlDataSource.Create(Cs);
        await using (var c = admin.CreateCommand($"CREATE SCHEMA {_schema}")) await c.ExecuteNonQueryAsync();
        await using var db = NpgsqlDataSource.Create(SchemaCs);
        await PostgresSchema.MigrateAsync(db, default);
        var store = new PostgresMarketDataStore(db);
        // Two recorded snapshots; replay clock sits between them.
        foreach (var (at, ask) in new[] { (T0.AddSeconds(-5), 0.95m), (T0.AddSeconds(30), 0.55m) })
            await store.SaveChainSnapshotAsync(new ChainSnapshot("SPX", new(2026, 11, 2), at, "tradier", Provenance.Observed,
                new IndexQuote("SPX", 7580m, at, at),
                [new OptionQuote("SPXW261102C07600000", "SPXW", OptionRight.Call, new(2026, 11, 2), 7600m, ask - 0.10m, ask, 10, 10,
                    at.AddSeconds(-1), at.AddSeconds(-1), 100, 500, new OptionGreeks(0.40m, null, null, null, null, at.AddSeconds(-30)))]), default);
        var bars = Enumerable.Range(0, 40).Select(i => new Bar(T0.AddMinutes(-30 + i), TimeSpan.FromMinutes(1), 7580, 7581, 7579, 7580.5m, null)).ToArray();
        await store.InsertBarsAsync("SPX", Timeframe.OneMinute, bars, "tradier", default);
    }

    private string SchemaCs => Cs + $";Search Path={_schema},public";

    public async Task DisposeAsync()
    {
        if (Cs is null) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(Cs);
        await using var c = admin.CreateCommand($"DROP SCHEMA {_schema} CASCADE");
        await c.ExecuteNonQueryAsync();
    }

    [SkippableFact] public async Task ReplaysRecordedDataAsOfTheReplayClock()
    {
        Skip.If(Cs is null, "SPXQ_TEST_PG not set");
        using var f = ApiFactory.With(new()
        {
            ["Platform:DataSource"] = "RecordedReplay", ["Platform:Storage"] = "Postgres",
            ["Platform:ReplayStart"] = T0.ToString("O"), ["ConnectionStrings:Postgres"] = SchemaCs,
        });
        var c = f.Authed();
        var sys = await c.GetFromJsonAsync<JsonElement>("/api/v8/system");
        Assert.Equal("RecordedReplay", sys.GetProperty("dataSource").GetString());
        Assert.Equal("Observed", sys.GetProperty("dataProvenance").GetString());

        // Selection must use the 09:59:55 snapshot (ask 0.95), not the 10:00:30 one (ask 0.55) from the replay "future".
        var sel = await (await c.PostAsJsonAsync("/api/v8/options/select", new { right = "CALL" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(sel.GetProperty("selected").ValueKind == JsonValueKind.Object, sel.ToString());
        Assert.Equal(0.95m, sel.GetProperty("selected").GetProperty("ask").GetDecimal());

        // Bars only up to the replay clock: 09:30..09:59 confirmed = 30 bars; none after 10:00.
        var bars = await c.GetFromJsonAsync<JsonElement>("/api/v8/market/bars/SPX?tf=1m");
        Assert.Equal(30, bars.GetProperty("count").GetInt32());
        Assert.Equal(0, bars.GetProperty("gaps").GetArrayLength());

        var list = await c.GetFromJsonAsync<JsonElement>($"/api/v8/market/snapshots?from={Uri.EscapeDataString(T0.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(T0.AddHours(1).ToString("O"))}");
        var id = list[0].GetProperty("id").GetInt64();
        var export = await c.GetFromJsonAsync<JsonElement>($"/api/v8/market/snapshots/{id}");
        Assert.Equal("Observed", export.GetProperty("provenance").GetString());
        Assert.Equal(1, export.GetProperty("options").GetArrayLength());

        var verify = await c.GetFromJsonAsync<JsonElement>("/api/v8/audit/verify");
        Assert.True(verify.GetProperty("intact").GetBoolean());
    }
}
