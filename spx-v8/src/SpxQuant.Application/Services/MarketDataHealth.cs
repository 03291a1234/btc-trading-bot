namespace SpxQuant.Application.Services;

/// <summary>
/// Tracks market-data request outcomes. Phase 6's stale-feed / disconnect kill switches consume this;
/// Phase 1 only reports it.
/// </summary>
public sealed class MarketDataHealth
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastSuccess, _lastFailure;
    private int _consecutiveFailures;
    private string? _lastError;

    public void RecordSuccess(DateTimeOffset at) { lock (_gate) { _lastSuccess = at; _consecutiveFailures = 0; } }
    public void RecordFailure(DateTimeOffset at, string error) { lock (_gate) { _lastFailure = at; _consecutiveFailures++; _lastError = error; } }

    public MarketDataHealthSnapshot Snapshot(DateTimeOffset now, TimeSpan maxSilence)
    {
        lock (_gate)
        {
            var healthy = _lastSuccess is { } s && now - s <= maxSilence && _consecutiveFailures == 0;
            return new(healthy ? "HEALTHY" : _lastSuccess is null && _lastFailure is null ? "NO_DATA_YET" : "UNHEALTHY",
                _lastSuccess, _lastFailure, _consecutiveFailures, _lastError);
        }
    }
}

public sealed record MarketDataHealthSnapshot(string Status, DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, int ConsecutiveFailures, string? LastError);
