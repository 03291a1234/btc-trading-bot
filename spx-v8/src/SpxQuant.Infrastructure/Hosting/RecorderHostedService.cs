using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpxQuant.Application.Abstractions;
using SpxQuant.Application.Services;

namespace SpxQuant.Infrastructure.Hosting;

public sealed class RecorderStatus
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, RecordResult Result)> _last = new();
    public bool Enabled { get; set; }
    public void Record(DateTimeOffset at, RecordResult r) { lock (_gate) _last[r.Step] = (at, r); }
    public object Snapshot() { lock (_gate) return new { enabled = Enabled, last = _last.ToDictionary(kv => kv.Key, kv => new { at = kv.Value.At, kv.Value.Result.Ok, kv.Value.Result.Detail }) }; }
}

/// <summary>Runs the recorder on independent chain/bar timers during the session. Failures are logged and reported, never fatal.</summary>
public sealed class RecorderHostedService(IServiceProvider services, RecorderOptions options, RecorderStatus status, IClock clock, ILogger<RecorderHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        status.Enabled = true;
        logger.LogInformation("Market-data recorder enabled: chain every {Chain}s, bars every {Bars}s", options.ChainIntervalSeconds, options.BarsIntervalSeconds);
        return Task.WhenAll(
            Loop(TimeSpan.FromSeconds(options.ChainIntervalSeconds), (r, ct) => r.RecordChainAsync(ct), stoppingToken),
            Loop(TimeSpan.FromSeconds(options.BarsIntervalSeconds), (r, ct) => r.RecordBarsAsync(ct), stoppingToken));
    }

    private async Task Loop(TimeSpan every, Func<MarketDataRecorder, CancellationToken, Task<RecordResult>> step, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(every);
        do
        {
            try
            {
                var recorder = services.GetRequiredService<MarketDataRecorder>();
                if (!recorder.InRecordingWindow(clock.UtcNow)) continue;
                var result = await step(recorder, ct);
                status.Record(clock.UtcNow, result);
                if (!result.Ok) logger.LogWarning("Recorder {Step} failed: {Detail}", result.Step, result.Detail);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Recorder step failed unexpectedly; will retry"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
