namespace SpxQuant.Application.Services;

public static class Streams
{
    public const string Chain = "chain";
    public const string Index = "index";
    public const string Bars = "bars1m";
    public const string Calendar = "calendar";
}

/// <summary>
/// Per-stream market-data health. Phase 6 kill switches (stale feed, disconnect) consume <see cref="IsStale"/>;
/// Phase 2 records and reports it.
/// </summary>
public sealed class MarketDataHealth
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.Ordinal);

    private sealed class StreamState
    {
        public DateTimeOffset? LastSuccess, LastFailure;
        public int ConsecutiveFailures;
        public string? LastError;
        public long Successes, Failures;
    }

    public void RecordSuccess(DateTimeOffset at, string stream = Streams.Chain)
    {
        lock (_gate) { var s = Get(stream); s.LastSuccess = at; s.ConsecutiveFailures = 0; s.Successes++; }
    }

    public void RecordFailure(DateTimeOffset at, string error, string stream = Streams.Chain)
    {
        lock (_gate) { var s = Get(stream); s.LastFailure = at; s.ConsecutiveFailures++; s.Failures++; s.LastError = error; }
    }

    public bool IsStale(string stream, DateTimeOffset now, TimeSpan maxSilence)
    {
        lock (_gate) return !_streams.TryGetValue(stream, out var s) || s.LastSuccess is not { } t || now - t > maxSilence || s.ConsecutiveFailures > 0;
    }

    /// <summary>Overall snapshot (chain stream) kept for the Phase 1 readiness contract.</summary>
    public MarketDataHealthSnapshot Snapshot(DateTimeOffset now, TimeSpan maxSilence) => StreamSnapshot(Streams.Chain, now, maxSilence);

    public MarketDataHealthSnapshot StreamSnapshot(string stream, DateTimeOffset now, TimeSpan maxSilence)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var s)) return new("NO_DATA_YET", null, null, 0, null, 0, 0);
            var healthy = s.LastSuccess is { } t && now - t <= maxSilence && s.ConsecutiveFailures == 0;
            return new(healthy ? "HEALTHY" : "UNHEALTHY", s.LastSuccess, s.LastFailure, s.ConsecutiveFailures, s.LastError, s.Successes, s.Failures);
        }
    }

    public IReadOnlyDictionary<string, MarketDataHealthSnapshot> All(DateTimeOffset now, TimeSpan maxSilence)
    {
        string[] names;
        lock (_gate) names = _streams.Keys.ToArray();
        return names.Order(StringComparer.Ordinal).ToDictionary(n => n, n => StreamSnapshot(n, now, maxSilence));
    }

    private StreamState Get(string stream) => _streams.TryGetValue(stream, out var s) ? s : _streams[stream] = new StreamState();
}

public sealed record MarketDataHealthSnapshot(string Status, DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, int ConsecutiveFailures,
    string? LastError, long Successes = 0, long Failures = 0);
