using SpxQuant.Infrastructure.Tradier;
using Xunit;

namespace SpxQuant.Infrastructure.Tests;

public class ReadOnlyGuardTests
{
    private static (HttpClient Client, RecordingHandler Network) Client()
    {
        var network = new RecordingHandler();
        return (new HttpClient(new ReadOnlyMarketDataGuard { InnerHandler = network }) { BaseAddress = new Uri("https://api.tradier.com") }, network);
    }

    [Theory]
    [InlineData("/v1/markets/quotes?symbols=SPX")]
    [InlineData("/v1/markets/options/chains?symbol=SPX&expiration=2026-11-02&greeks=true")]
    [InlineData("/v1/markets/clock")]
    [InlineData("/v1/markets/calendar")]
    public async Task MarketDataGetsAreAllowed(string path)
    {
        var (client, network) = Client();
        await client.GetAsync(path);
        Assert.Single(network.Seen);
    }

    [Theory]
    [InlineData("POST", "/v1/accounts/ABC/orders")]
    [InlineData("GET", "/v1/accounts/ABC/positions")]
    [InlineData("GET", "/v1/accounts/ABC/balances")]
    [InlineData("GET", "/v1/user/profile")]
    [InlineData("DELETE", "/v1/accounts/ABC/orders/1")]
    [InlineData("PUT", "/v1/accounts/ABC/orders/1")]
    [InlineData("POST", "/v1/markets/quotes")]
    [InlineData("GET", "/v1/markets/../accounts/ABC/orders")]
    [InlineData("GET", "/v1/watchlists")]
    public async Task EverythingElseIsBlockedBeforeTheNetwork(string method, string path)
    {
        var (client, network) = Client();
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        await Assert.ThrowsAsync<BrokerageAccessBlockedException>(() => client.SendAsync(request));
        Assert.Empty(network.Seen);
    }

    [Fact]
    public async Task PlainHttpIsBlocked()
    {
        var network = new RecordingHandler();
        var client = new HttpClient(new ReadOnlyMarketDataGuard { InnerHandler = network });
        await Assert.ThrowsAsync<BrokerageAccessBlockedException>(() => client.GetAsync("http://api.tradier.com/v1/markets/clock"));
        Assert.Empty(network.Seen);
    }
}
