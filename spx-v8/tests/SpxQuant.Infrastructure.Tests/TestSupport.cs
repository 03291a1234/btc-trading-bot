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
