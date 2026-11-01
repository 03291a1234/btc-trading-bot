using SpxQuant.Application.Abstractions;

namespace SpxQuant.Infrastructure.Time;

public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

/// <summary>Replay clock pinned to the snapshot's capture time plus elapsed wall time (bounded drift is visible in freshness checks).</summary>
public sealed class ReplayClock(DateTimeOffset start, bool advance = false) : IClock
{
    private readonly DateTimeOffset _wallStart = DateTimeOffset.UtcNow;
    public DateTimeOffset UtcNow => advance ? start + (DateTimeOffset.UtcNow - _wallStart) : start;
}
