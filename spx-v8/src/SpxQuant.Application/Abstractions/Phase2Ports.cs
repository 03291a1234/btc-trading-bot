using SpxQuant.Domain.Calendar;
using SpxQuant.Domain.Market;

namespace SpxQuant.Application.Abstractions;

/// <summary>Read-only broker metadata used for cross-checks. Implemented by Tradier only.</summary>
public interface IBrokerSessionInfo
{
    Task<BrokerClock> GetClockAsync(CancellationToken ct);
    Task<IReadOnlyList<CalendarDay>> GetCalendarMonthAsync(int year, int month, CancellationToken ct);
}

public sealed record BrokerClock(DateOnly Date, string State, string? NextChange, string? NextState, DateTimeOffset ReceivedAt);

/// <summary>Historical/intraday bars. Implementations must return only bars that the vendor reports as complete.</summary>
public interface IBarSource
{
    Task<IReadOnlyList<Bar>> GetOneMinuteBarsAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

public sealed record StoredSnapshotInfo(long Id, string Underlying, DateOnly Expiration, DateTimeOffset CapturedAt, string Source,
    string Provenance, int OptionCount, decimal? IndexLast, string ContentSha256);

/// <summary>Durable market-data store (PostgreSQL/TimescaleDB).</summary>
public interface IMarketDataStore
{
    /// <summary>Insert-or-ignore: an existing bar is never overwritten. Returns how many rows were new.</summary>
    Task<int> InsertBarsAsync(string symbol, Timeframe timeframe, IReadOnlyList<Bar> bars, string source, CancellationToken ct);
    Task<IReadOnlyList<Bar>> GetBarsAsync(string symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<StoredSnapshotInfo> SaveChainSnapshotAsync(ChainSnapshot snapshot, CancellationToken ct);
    /// <summary>Latest snapshot captured at or before <paramref name="asOf"/> (never a later one: no look-ahead).</summary>
    Task<ChainSnapshot?> GetSnapshotAsOfAsync(string underlying, DateOnly expiration, DateTimeOffset asOf, CancellationToken ct);
    Task<ChainSnapshot?> GetSnapshotAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<StoredSnapshotInfo>> ListSnapshotsAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct);
}
