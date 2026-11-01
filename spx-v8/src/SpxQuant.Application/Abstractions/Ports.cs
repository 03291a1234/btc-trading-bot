using SpxQuant.Domain.Audit;
using SpxQuant.Domain.Calendar;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;

namespace SpxQuant.Application.Abstractions;

public interface IClock { DateTimeOffset UtcNow { get; } }

public enum DataSourceKind { Replay, TradierReadOnly }

/// <summary>
/// Read-only market data port. There is deliberately no order, account or position method anywhere in v8;
/// architecture tests fail if one is added.
/// </summary>
public interface IMarketDataProvider
{
    DataSourceKind Kind { get; }
    /// <summary>Provenance of the data this provider returns (Observed for a vendor, Synthetic for fixtures).</summary>
    Provenance DataProvenance { get; }
    Task<IndexQuote?> GetIndexQuoteAsync(string symbol, CancellationToken ct);
    Task<IReadOnlyList<OptionQuote>> GetOptionChainAsync(string underlying, DateOnly expiration, CancellationToken ct);
}

public interface IAuditLog
{
    Task<AuditRecord> AppendAsync(string kind, string? subject, object payload, CancellationToken ct);
    Task<IReadOnlyList<AuditRecord>> ReadAsync(long afterSequence, int limit, CancellationToken ct);
    Task<string?> VerifyAsync(CancellationToken ct);
}

public interface ITradingCalendarProvider { TradingCalendar Calendar { get; } }

public sealed class MarketDataUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
