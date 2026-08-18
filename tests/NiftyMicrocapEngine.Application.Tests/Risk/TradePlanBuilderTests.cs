using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Risk;

public class TradePlanBuilderTests
{
    private readonly TradePlanBuilder _builder = new(Options.Create(new RiskManagerOptions { StopAtrMultiple = 1.5m }));

    [Fact]
    public void Build_Long_StructuralStopTighterThanAtrFloor_UsesAtrFloor()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 98m, 2m, null, "CHoCH below 97", Array.Empty<TimeSpan>());

        var plan = _builder.Build(request);

        Assert.Equal(97m, plan.StopLoss);
    }

    [Fact]
    public void Build_Long_StructuralStopWiderThanAtrFloor_UsesStructuralStop()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 90m, 2m, null, "CHoCH below 90", Array.Empty<TimeSpan>());

        var plan = _builder.Build(request);

        Assert.Equal(90m, plan.StopLoss);
    }

    [Fact]
    public void Build_Short_StructuralStopTighterThanAtrFloor_UsesAtrFloor()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bearish, 100m, 102m, 2m, null, "CHoCH above 103", Array.Empty<TimeSpan>());

        var plan = _builder.Build(request);

        Assert.Equal(103m, plan.StopLoss);
    }

    [Fact]
    public void Build_NoStructuralStop_FallsBackToAtrFloorOnly()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, null, 2m, null, "N/A", Array.Empty<TimeSpan>());

        var plan = _builder.Build(request);

        Assert.Equal(97m, plan.StopLoss);
    }

    [Fact]
    public void Build_Long_TargetsAreOneTwoThreeR_WhenNoZoneNearer()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 90m, 2m, null, "N/A", Array.Empty<TimeSpan>());

        var plan = _builder.Build(request);

        Assert.Equal(110m, plan.Target1);
        Assert.Equal(120m, plan.Target2);
        Assert.Equal(130m, plan.Target3);
    }

    [Fact]
    public void Build_Long_ZoneNearerThanTarget1_CapsTarget1AtZone()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 90m, 2m, 105m, "N/A", Array.Empty<TimeSpan>());

        var plan = _builder.Build(request);

        Assert.Equal(105m, plan.Target1);
        Assert.Equal(120m, plan.Target2);
        Assert.Equal(130m, plan.Target3);
    }

    [Fact]
    public void Build_Long_ZoneOnWrongSideOfEntry_IsIgnoredForTargets()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 90m, 2m, 95m, "N/A", Array.Empty<TimeSpan>());

        var plan = _builder.Build(request);

        Assert.Equal(110m, plan.Target1);
    }

    [Fact]
    public void Build_RiskRewardRatio_ComputedFromTarget1()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 90m, 2m, null, "N/A", Array.Empty<TimeSpan>());

        var plan = _builder.Build(request);

        Assert.Equal(1.0m, plan.RiskRewardRatio);
    }

    [Fact]
    public void Build_RiskPercent_ComputedCorrectly()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 90m, 2m, null, "N/A", Array.Empty<TimeSpan>());

        var plan = _builder.Build(request);

        Assert.Equal(0.1m, plan.RiskPercent);
    }

    [Fact]
    public void Build_FewerThanThreeQualifyingLegs_ReturnsNullDurationWithFlag()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 90m, 2m, null, "N/A",
            new[] { TimeSpan.FromDays(5), TimeSpan.FromDays(7) });

        var plan = _builder.Build(request);

        Assert.Null(plan.EstimatedDuration);
        Assert.Equal("InsufficientHistoryForDurationEstimate", plan.DurationDataQualityFlag);
    }

    [Fact]
    public void Build_ThreeOrMoreQualifyingLegs_ComputesAverageDuration()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 90m, 2m, null, "N/A",
            new[] { TimeSpan.FromDays(6), TimeSpan.FromDays(9), TimeSpan.FromDays(12) });

        var plan = _builder.Build(request);

        Assert.NotNull(plan.EstimatedDuration);
        Assert.Null(plan.DurationDataQualityFlag);
        Assert.Equal(TimeSpan.FromDays(9), plan.EstimatedDuration!.Value);
    }

    [Fact]
    public void Build_UsesConfiguredAtrStopMultiple_NotHardcoded()
    {
        // With a much larger configured multiple, the ATR floor should push
        // further from entry than the default 1.5x would, even with the same
        // structural stop and ATR inputs as another test above.
        var wideBuilder = new TradePlanBuilder(Options.Create(new RiskManagerOptions { StopAtrMultiple = 5m }));
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 98m, 2m, null, "N/A", Array.Empty<TimeSpan>());

        var plan = wideBuilder.Build(request);

        // ATR floor = 100 - 5*2 = 90, which is wider than the structural stop (98) -> wins.
        Assert.Equal(90m, plan.StopLoss);
    }

    [Fact]
    public void Build_ZeroRiskPerShare_ThrowsRatherThanProduceUndefinedRMultiple()
    {
        var request = new TradePlanRequest(
            TrendDirection.Bullish, 100m, 100m, 0m, null, "N/A", Array.Empty<TimeSpan>());

        Assert.Throws<InvalidOperationException>(() => _builder.Build(request));
    }
}
