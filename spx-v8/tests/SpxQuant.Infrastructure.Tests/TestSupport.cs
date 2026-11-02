using System.Net;
using SpxQuant.Application.Abstractions;

namespace SpxQuant.Infrastructure.Tests;

internal sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }

/// <summary>Terminal handler that records every request that reached the "network".</summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
{
    public List<HttpRequestMessage> Seen { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Seen.Add(request);
        return Task.FromResult(respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}

internal static class RepoPaths
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SpxQuant.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
        }
    }
}

internal static class Fixtures
{
    public static SpxQuant.Domain.Market.OptionQuote Quote(DateTimeOffset at) => new("SPXW261102C07600000", "SPXW", SpxQuant.Domain.Market.OptionRight.Call,
        new DateOnly(2026, 11, 2), 7600m, 0.80m, 0.90m, 10, 10, at.AddSeconds(-2), at.AddSeconds(-2), 100, 500,
        new SpxQuant.Domain.Market.OptionGreeks(0.40m, 0.01m, -1m, 0.1m, 0.16m, at.AddSeconds(-60)));
}
