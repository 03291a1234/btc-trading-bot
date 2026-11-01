namespace SpxQuant.Domain.Risk;

/// <summary>
/// Hard ceilings confirmed by the user in v7 (AGENTS.md). Configuration may tighten these; configuration that would
/// loosen them is rejected at startup (v7 silently clamped; v8 fails loudly so a mis-typed config is noticed).
/// Raising a ceiling is a code change requiring explicit review.
/// </summary>
public static class RiskCeilings
{
    public const decimal MaxPlannedLossPerTrade = 100m;
    public const decimal MaxWorstCaseLossPerTrade = 100m;
    public const decimal MaxSpreadPerShare = 0.50m;
    public const decimal MaxAggregatePremium = 100m;
    public const decimal MaxDailyLoss = 200m;
    public const int MaxOpenPositions = 1;
    public const int MaxTradesPerDay = 3;
    public const int ContractMultiplier = 100;
}

public sealed record RiskLimits
{
    public decimal TargetAbsDelta { get; init; } = 0.40m;
    public decimal MaxDeltaDeviation { get; init; } = 0.15m;
    public decimal MaxSpreadPerShare { get; init; } = RiskCeilings.MaxSpreadPerShare;
    public decimal MaxPlannedLossPerTrade { get; init; } = RiskCeilings.MaxPlannedLossPerTrade;
    public decimal MaxWorstCaseLossPerTrade { get; init; } = RiskCeilings.MaxWorstCaseLossPerTrade;
    public decimal MaxAggregatePremium { get; init; } = RiskCeilings.MaxAggregatePremium;
    public decimal MaxDailyLoss { get; init; } = RiskCeilings.MaxDailyLoss;
    public int MaxOpenPositions { get; init; } = RiskCeilings.MaxOpenPositions;
    public int MaxTradesPerDay { get; init; } = RiskCeilings.MaxTradesPerDay;
    public int MaxConsecutiveLosses { get; init; } = 3;
    public TimeSpan CooldownAfterLoss { get; init; } = TimeSpan.FromMinutes(15);
    public int ContractMultiplier { get; init; } = RiskCeilings.ContractMultiplier;
    /// <summary>ESTIMATED per-contract, per-side fee. Not a verified brokerage schedule.</summary>
    public decimal FeePerContractPerSide { get; init; } = 1.00m;
    /// <summary>ESTIMATED adverse slippage per share applied to a stop exit (beyond exiting at the bid).</summary>
    public decimal StopSlippagePerShare { get; init; } = 0.05m;
    /// <summary>Stop trigger as a fraction of entry premium lost (v7: 30% stop).</summary>
    public decimal StopLossFraction { get; init; } = 0.30m;
    /// <summary>Target as a fraction of entry premium gained (v7: 50% target).</summary>
    public decimal TakeProfitFraction { get; init; } = 0.50m;
    public long MinOpenInterest { get; init; } = 10;
    public long MinVolume { get; init; } = 1;
    public long MinDisplayedSize { get; init; } = 1;

    /// <summary>Returns every violation; an empty list means the limits are acceptable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        void Range(string name, decimal v, decimal min, decimal max) { if (v < min || v > max) e.Add($"{name}={v} must be within [{min}, {max}]"); }
        void IRange(string name, int v, int min, int max) { if (v < min || v > max) e.Add($"{name}={v} must be within [{min}, {max}]"); }
        Range(nameof(TargetAbsDelta), TargetAbsDelta, 0.05m, 0.95m);
        Range(nameof(MaxDeltaDeviation), MaxDeltaDeviation, 0m, 0.30m);
        Range(nameof(MaxSpreadPerShare), MaxSpreadPerShare, 0.01m, RiskCeilings.MaxSpreadPerShare);
        Range(nameof(MaxPlannedLossPerTrade), MaxPlannedLossPerTrade, 1m, RiskCeilings.MaxPlannedLossPerTrade);
        Range(nameof(MaxWorstCaseLossPerTrade), MaxWorstCaseLossPerTrade, 1m, RiskCeilings.MaxWorstCaseLossPerTrade);
        Range(nameof(MaxAggregatePremium), MaxAggregatePremium, 1m, RiskCeilings.MaxAggregatePremium);
        Range(nameof(MaxDailyLoss), MaxDailyLoss, 1m, RiskCeilings.MaxDailyLoss);
        IRange(nameof(MaxOpenPositions), MaxOpenPositions, 1, RiskCeilings.MaxOpenPositions);
        IRange(nameof(MaxTradesPerDay), MaxTradesPerDay, 1, RiskCeilings.MaxTradesPerDay);
        IRange(nameof(MaxConsecutiveLosses), MaxConsecutiveLosses, 1, 10);
        if (CooldownAfterLoss < TimeSpan.Zero) e.Add("CooldownAfterLoss must be nonnegative");
        if (ContractMultiplier != RiskCeilings.ContractMultiplier) e.Add("ContractMultiplier must be 100 for SPX/SPXW");
        Range(nameof(FeePerContractPerSide), FeePerContractPerSide, 0m, 25m);
        Range(nameof(StopSlippagePerShare), StopSlippagePerShare, 0m, 5m);
        Range(nameof(StopLossFraction), StopLossFraction, 0.05m, 1m);
        Range(nameof(TakeProfitFraction), TakeProfitFraction, 0.05m, 10m);
        if (MinOpenInterest < 0 || MinVolume < 0 || MinDisplayedSize < 1) e.Add("Liquidity minimums must be nonnegative and displayed size >= 1");
        return e;
    }
}

/// <summary>
/// Risk of a single long option contract, distinguishing the stop-based PLANNED loss from the WORST-CASE premium loss.
/// Both are ESTIMATES: stops can gap, fills can be worse, fees may differ.
/// </summary>
public sealed record TradeRiskProfile(
    int Quantity,
    decimal EntryPricePerShare,
    decimal StopTriggerPerShare,
    decimal TargetPerShare,
    decimal EntryDebit,
    decimal PlannedLoss,
    decimal WorstCaseLoss,
    decimal RoundTripFees)
{
    public static TradeRiskProfile ForLongOption(decimal entryAsk, int quantity, RiskLimits limits)
    {
        if (entryAsk <= 0) throw new ArgumentOutOfRangeException(nameof(entryAsk));
        if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
        var m = limits.ContractMultiplier * quantity;
        var fees = 2m * limits.FeePerContractPerSide * quantity;
        var stop = entryAsk * (1m - limits.StopLossFraction);
        var target = entryAsk * (1m + limits.TakeProfitFraction);
        var stopFill = Math.Max(0m, stop - limits.StopSlippagePerShare);
        var planned = (entryAsk - stopFill) * m + fees;
        var worst = entryAsk * m + fees; // option expires worthless / no bid
        return new(quantity, entryAsk, Round(stop), Round(target), entryAsk * m + limits.FeePerContractPerSide * quantity,
            Round(planned), Round(worst), fees);
    }

    private static decimal Round(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
}
