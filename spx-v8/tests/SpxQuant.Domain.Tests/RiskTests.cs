using SpxQuant.Domain.Risk;
using Xunit;

namespace SpxQuant.Domain.Tests;

public class RiskTests
{
    [Fact] public void DefaultsMatchV7ConfirmedLimitsAndAreValid()
    {
        var l = new RiskLimits();
        Assert.Empty(l.Validate());
        Assert.Equal(0.40m, l.TargetAbsDelta);
        Assert.Equal(0.50m, l.MaxSpreadPerShare);
        Assert.Equal(100m, l.MaxPlannedLossPerTrade);
        Assert.Equal(100, l.ContractMultiplier);
    }
    [Theory]
    [InlineData("spread")] [InlineData("planned")] [InlineData("worst")] [InlineData("premium")] [InlineData("daily")] [InlineData("positions")]
    public void LooseningAnyCeilingIsRejected(string which)
    {
        var l = which switch
        {
            "spread" => new RiskLimits { MaxSpreadPerShare = 0.51m },
            "planned" => new RiskLimits { MaxPlannedLossPerTrade = 150m },
            "worst" => new RiskLimits { MaxWorstCaseLossPerTrade = 101m },
            "premium" => new RiskLimits { MaxAggregatePremium = 500m },
            "daily" => new RiskLimits { MaxDailyLoss = 1000m },
            _ => new RiskLimits { MaxOpenPositions = 2 },
        };
        Assert.NotEmpty(l.Validate());
    }
    [Fact] public void TighteningIsAllowed() => Assert.Empty(new RiskLimits { MaxSpreadPerShare = 0.20m, MaxPlannedLossPerTrade = 50m, MaxDailyLoss = 100m }.Validate());
    [Fact] public void MultiplierIsFixed() => Assert.NotEmpty(new RiskLimits { ContractMultiplier = 10 }.Validate());

    [Fact] public void PlannedAndWorstCaseLossAreDistinct()
    {
        // ask 0.90, 30% stop -> trigger 0.63, minus $0.05 slippage -> 0.58 fill; fees $1/side.
        var p = TradeRiskProfile.ForLongOption(0.90m, 1, new RiskLimits());
        Assert.Equal(0.63m, p.StopTriggerPerShare);
        Assert.Equal(1.35m, p.TargetPerShare);
        Assert.Equal(34m, p.PlannedLoss);      // (0.90-0.58)*100 + 2
        Assert.Equal(92m, p.WorstCaseLoss);    // 0.90*100 + 2
        Assert.Equal(91m, p.EntryDebit);       // 0.90*100 + 1
        Assert.True(p.PlannedLoss < p.WorstCaseLoss);
    }
    [Fact] public void StopFillNeverBelowZero()
    {
        var p = TradeRiskProfile.ForLongOption(0.05m, 1, new RiskLimits { StopLossFraction = 1m });
        Assert.Equal(p.WorstCaseLoss, p.PlannedLoss);
    }
    [Fact] public void InvalidInputsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TradeRiskProfile.ForLongOption(0m, 1, new RiskLimits()));
        Assert.Throws<ArgumentOutOfRangeException>(() => TradeRiskProfile.ForLongOption(1m, 0, new RiskLimits()));
    }
}
