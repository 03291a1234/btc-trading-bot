using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;
using SpxQuant.Domain.Risk;

namespace SpxQuant.Domain.Options;

public enum RejectionReason
{
    WrongExpiration,
    NotPmSettled,
    WrongRight,
    InvalidQuote,
    StaleQuote,
    /// <summary>Vendor Greeks missing a timestamp or older than policy. Kept separate: vendor Greeks may refresh far less often than quotes.</summary>
    StaleGreeks,
    InsufficientLiquidity,
    DeltaOutOfRange,
    SpreadTooWide,
    PremiumExposureExceeded,
    PlannedLossExceeded,
    WorstCaseLossExceeded,
    /// <summary>The current bid is already at/below the stop trigger: entering at the ask would stop out immediately (v7 defect).</summary>
    StopInsideSpread,
}

public sealed record ContractEvaluation(
    OptionQuote Quote,
    IReadOnlyList<RejectionReason> Rejections,
    IReadOnlySet<QuoteIssue> QuoteIssues,
    TradeRiskProfile? Risk,
    decimal? DeltaDistance)
{
    public bool Eligible => Rejections.Count == 0;
}

public sealed record ContractSelection(
    ContractEvaluation? Selected,
    IReadOnlyList<ContractEvaluation> Ranked,
    IReadOnlyDictionary<RejectionReason, int> RejectionCounts,
    int Received,
    string Summary,
    Provenance RiskProvenance = Provenance.Estimated);

/// <summary>
/// Hard-constraint filter plus deterministic ranking for 0DTE SPXW long calls/puts.
/// Unlike v7's sequential filter, every contract is evaluated against every rule, so rejection counts are
/// independent and each contract carries all of its reasons.
/// Ranking (Phase 1, ported from v7): delta proximity, then spread, then displayed bid size.
/// Calibrated multi-factor ranking is Phase 5 work.
/// </summary>
public sealed class ContractSelector(RiskLimits limits, QuoteValidationPolicy quotePolicy)
{
    private static readonly HashSet<QuoteIssue> StaleIssues =
        [QuoteIssue.MissingBidTimestamp, QuoteIssue.MissingAskTimestamp, QuoteIssue.StaleBid, QuoteIssue.StaleAsk, QuoteIssue.FutureTimestamp];
    private static readonly HashSet<QuoteIssue> LiquidityIssues = [QuoteIssue.ZeroBidSize, QuoteIssue.ZeroAskSize];

    public ContractSelection Select(IEnumerable<OptionQuote> chain, OptionRight right, DateTimeOffset now, decimal currentAggregatePremium = 0m)
    {
        var today = EasternTime.DateOf(now);
        var evaluations = chain.Select(q => Evaluate(q, right, today, now, currentAggregatePremium)).ToList();
        var counts = Enum.GetValues<RejectionReason>().ToDictionary(r => r, r => evaluations.Count(e => e.Rejections.Contains(r)));
        var ranked = evaluations.Where(e => e.Eligible)
            .OrderBy(e => e.DeltaDistance)
            .ThenBy(e => e.Quote.Spread)
            .ThenByDescending(e => e.Quote.BidSize)
            .ThenBy(e => e.Quote.Symbol, StringComparer.Ordinal)
            .ToList();
        var selected = ranked.FirstOrDefault();
        var summary = selected is null
            ? $"No eligible contract among {evaluations.Count}; rejected rather than loosening limits."
            : $"Selected {selected.Quote.Symbol}; {ranked.Count} of {evaluations.Count} eligible.";
        return new(selected, ranked, counts, evaluations.Count, summary);
    }

    public ContractEvaluation Evaluate(OptionQuote q, OptionRight right, DateOnly today, DateTimeOffset now, decimal currentAggregatePremium = 0m)
    {
        var reasons = new List<RejectionReason>();
        var issues = QuoteValidator.Validate(q, now, quotePolicy, today);

        if (q.Expiration != today) reasons.Add(RejectionReason.WrongExpiration);
        var pm = string.Equals(q.Root, "SPXW", StringComparison.OrdinalIgnoreCase) ||
                 (string.Equals(q.Root, "SPX", StringComparison.OrdinalIgnoreCase) && q.IsPmSettled);
        if (!pm) reasons.Add(RejectionReason.NotPmSettled);
        if (q.Right != right) reasons.Add(RejectionReason.WrongRight);

        var structural = issues.Except(StaleIssues).Except(LiquidityIssues).Where(i => i is not (QuoteIssue.Expired or QuoteIssue.InvalidDelta or QuoteIssue.StaleGreeks or QuoteIssue.MissingGreeks)).ToArray();
        if (structural.Length > 0) reasons.Add(RejectionReason.InvalidQuote);
        if (issues.Overlaps(StaleIssues)) reasons.Add(RejectionReason.StaleQuote);
        if (issues.Contains(QuoteIssue.StaleGreeks)) reasons.Add(RejectionReason.StaleGreeks);
        if (issues.Overlaps(LiquidityIssues) || q.BidSize < limits.MinDisplayedSize || q.AskSize < limits.MinDisplayedSize ||
            q.OpenInterest is not { } oi || oi < limits.MinOpenInterest || q.Volume is not { } vol || vol < limits.MinVolume)
            reasons.Add(RejectionReason.InsufficientLiquidity);

        decimal? distance = null;
        if (q.Greeks?.Delta is { } d && !issues.Contains(QuoteIssue.InvalidDelta))
        {
            distance = Math.Abs(Math.Abs(d) - limits.TargetAbsDelta);
            if (distance > limits.MaxDeltaDeviation) reasons.Add(RejectionReason.DeltaOutOfRange);
        }
        else reasons.Add(RejectionReason.DeltaOutOfRange);

        if (q.Spread is not { } spread || spread > limits.MaxSpreadPerShare) reasons.Add(RejectionReason.SpreadTooWide);

        TradeRiskProfile? risk = null;
        if (q.Ask is { } ask && ask > 0)
        {
            risk = TradeRiskProfile.ForLongOption(ask, 1, limits);
            if (currentAggregatePremium + risk.EntryDebit > limits.MaxAggregatePremium) reasons.Add(RejectionReason.PremiumExposureExceeded);
            if (risk.PlannedLoss > limits.MaxPlannedLossPerTrade) reasons.Add(RejectionReason.PlannedLossExceeded);
            if (risk.WorstCaseLoss > limits.MaxWorstCaseLossPerTrade) reasons.Add(RejectionReason.WorstCaseLossExceeded);
            if (q.Bid is { } bid && bid <= risk.StopTriggerPerShare) reasons.Add(RejectionReason.StopInsideSpread);
        }
        return new(q, reasons, issues, risk, distance);
    }
}
