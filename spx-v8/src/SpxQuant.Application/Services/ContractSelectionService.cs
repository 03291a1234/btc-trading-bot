using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;
using SpxQuant.Domain.Options;
using SpxQuant.Domain.Risk;

namespace SpxQuant.Application.Services;

public sealed record SelectionReport(
    DateTimeOffset EvaluatedAt,
    DateOnly Expiration,
    string Right,
    bool EntryWindowOpen,
    string EntryWindowReason,
    string DataSource,
    Provenance QuoteProvenance,
    Provenance RiskProvenance,
    ContractSelection Selection,
    long AuditSequence,
    string Note);

/// <summary>
/// Evaluates the 0DTE chain against hard constraints and records the decision in the audit log.
/// It never creates an order or position: Phase 1 has no execution path at all.
/// </summary>
public sealed class ContractSelectionService(
    IMarketDataProvider market,
    ITradingCalendarProvider calendar,
    IClock clock,
    IAuditLog audit,
    MarketDataHealth health,
    RiskLimits limits,
    QuoteValidationPolicy quotePolicy)
{
    public const string Underlying = "SPX";

    public async Task<SelectionReport> EvaluateAsync(OptionRight right, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var today = EasternTime.DateOf(now);
        IReadOnlyList<OptionQuote> chain;
        try
        {
            chain = await market.GetOptionChainAsync(Underlying, today, ct);
            health.RecordSuccess(clock.UtcNow);
        }
        catch (MarketDataUnavailableException ex)
        {
            health.RecordFailure(clock.UtcNow, ex.Message);
            await audit.AppendAsync("SELECTION_FAILED", null, new { right = right.ToString().ToUpperInvariant(), reason = "MARKET_DATA_UNAVAILABLE" }, ct);
            throw;
        }
        // Evaluate at the time the data arrived, not the request start, so slow responses cannot look fresher.
        var evaluatedAt = clock.UtcNow;
        var entryOpen = calendar.Calendar.IsEntryAllowed(evaluatedAt, out var reason);
        var selection = new ContractSelector(limits, quotePolicy).Select(chain, right, evaluatedAt);
        var record = await audit.AppendAsync("CONTRACT_SELECTION", selection.Selected?.Quote.Symbol, new
        {
            right = right.ToString().ToUpperInvariant(),
            expiration = today,
            evaluatedAt,
            entryWindowOpen = entryOpen,
            entryWindowReason = reason,
            received = selection.Received,
            eligible = selection.Ranked.Count,
            selected = selection.Selected?.Quote.Symbol,
            plannedLoss = selection.Selected?.Risk?.PlannedLoss,
            worstCaseLoss = selection.Selected?.Risk?.WorstCaseLoss,
            rejectionCounts = selection.RejectionCounts.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            dataSource = market.Kind.ToString(),
        }, ct);
        return new(evaluatedAt, today, right.ToString().ToUpperInvariant(), entryOpen, reason, market.Kind.ToString(),
            market.DataProvenance, Provenance.Estimated, selection, record.Sequence,
            "Evaluation only. No order or paper position is created in Phase 1. Risk figures are estimates (fees, slippage, stop gaps).");
    }
}
