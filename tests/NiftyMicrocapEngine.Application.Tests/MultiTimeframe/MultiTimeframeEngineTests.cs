using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.MultiTimeframe;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.MultiTimeframe;

public class MultiTimeframeEngineTests
{
    private static MultiTimeframeEngine BuildEngine(MultiTimeframeWeights? weights = null)
    {
        var options = new MultiTimeframeOptions { Weights = weights ?? new MultiTimeframeWeights() };
        return new MultiTimeframeEngine(Options.Create(options));
    }

    [Fact]
    public void Evaluate_AllTimeframesAligned_ReturnsFullScore()
    {
        var engine = BuildEngine();
        var signals = new[]
        {
            new TimeframeSignal(Timeframe.Weekly, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.Daily, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.H1, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.M30, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.M15, TrendDirection.Bullish, true)
        };

        var result = engine.Evaluate(signals, TrendDirection.Bullish);

        Assert.Equal(100m, result.AlignmentScore);
        Assert.False(result.WasRenormalized);
        Assert.Empty(result.UnavailableTimeframes);
    }

    [Fact]
    public void Evaluate_NoTimeframesAligned_ReturnsZero()
    {
        var engine = BuildEngine();
        var signals = new[]
        {
            new TimeframeSignal(Timeframe.Weekly, TrendDirection.Bearish, true),
            new TimeframeSignal(Timeframe.Daily, TrendDirection.Bearish, true)
        };

        var result = engine.Evaluate(signals, TrendDirection.Bullish);

        Assert.Equal(0m, result.AlignmentScore);
    }

    [Fact]
    public void Evaluate_PartialAlignment_MatchesDefaultWeightSum()
    {
        // Defaults: Weekly 40, Daily 35, H1 10, M30 8, M15 7 (sums to 100).
        // Weekly+Daily aligned = 75.
        var engine = BuildEngine();
        var signals = new[]
        {
            new TimeframeSignal(Timeframe.Weekly, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.Daily, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.H1, TrendDirection.Bearish, true),
            new TimeframeSignal(Timeframe.M30, TrendDirection.Bearish, true),
            new TimeframeSignal(Timeframe.M15, TrendDirection.Bearish, true)
        };

        var result = engine.Evaluate(signals, TrendDirection.Bullish);

        Assert.Equal(75m, result.AlignmentScore);
    }

    [Fact]
    public void Evaluate_OneTimeframeUnavailable_RenormalizesRatherThanUnderstating()
    {
        // M15 (weight 7) unavailable. Remaining total = 93. Weekly+Daily aligned (75/93*100).
        var engine = BuildEngine();
        var signals = new[]
        {
            new TimeframeSignal(Timeframe.Weekly, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.Daily, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.H1, TrendDirection.Bearish, true),
            new TimeframeSignal(Timeframe.M30, TrendDirection.Bearish, true),
            new TimeframeSignal(Timeframe.M15, TrendDirection.Ranging, false) // unavailable
        };

        var result = engine.Evaluate(signals, TrendDirection.Bullish);

        var expected = 75m / 93m * 100m;
        Assert.Equal(expected, result.AlignmentScore, 6);
        Assert.True(result.WasRenormalized);
        Assert.Contains(Timeframe.M15, result.UnavailableTimeframes);
    }

    [Fact]
    public void Evaluate_UnavailableTimeframe_NeverCountedAsMisaligned()
    {
        // Regression guard for the specific failure mode §12 calls out: a missing
        // timeframe must be EXCLUDED, not treated as "trend = Ranging = doesn't
        // match = 0 contribution but still divides into the total." Compare full
        // alignment with one timeframe missing vs. explicitly present-but-Ranging.
        var engineA = BuildEngine();
        var allAvailableButOneRanging = new[]
        {
            new TimeframeSignal(Timeframe.Weekly, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.Daily, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.H1, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.M30, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.M15, TrendDirection.Ranging, true) // present, but doesn't match -> 0 contribution, DOES count in denominator
        };
        var resultRangingPresent = engineA.Evaluate(allAvailableButOneRanging, TrendDirection.Bullish);

        var engineB = BuildEngine();
        var oneUnavailable = new[]
        {
            new TimeframeSignal(Timeframe.Weekly, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.Daily, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.H1, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.M30, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.M15, TrendDirection.Ranging, false) // unavailable -> excluded from denominator entirely
        };
        var resultUnavailable = engineB.Evaluate(oneUnavailable, TrendDirection.Bullish);

        // Unavailable (renormalized, excluded) must score HIGHER than
        // present-but-Ranging (counted in denominator, contributes 0) — that's
        // the entire point of renormalization vs. silently understating.
        Assert.True(resultUnavailable.AlignmentScore > resultRangingPresent.AlignmentScore);
    }

    [Fact]
    public void Evaluate_NoDataAvailableAtAll_ReturnsZeroWithoutThrowing()
    {
        var engine = BuildEngine();
        var signals = new[]
        {
            new TimeframeSignal(Timeframe.Weekly, TrendDirection.Bullish, false),
            new TimeframeSignal(Timeframe.Daily, TrendDirection.Bullish, false)
        };

        var result = engine.Evaluate(signals, TrendDirection.Bullish);

        Assert.Equal(0m, result.AlignmentScore);
    }

    [Fact]
    public void Evaluate_CustomWeights_AreRespected()
    {
        var customWeights = new MultiTimeframeWeights { Weekly = 50m, Daily = 50m, H1 = 0m, M30 = 0m, M15 = 0m };
        var engine = BuildEngine(customWeights);

        var signals = new[]
        {
            new TimeframeSignal(Timeframe.Weekly, TrendDirection.Bullish, true),
            new TimeframeSignal(Timeframe.Daily, TrendDirection.Bearish, true)
        };

        var result = engine.Evaluate(signals, TrendDirection.Bullish);

        Assert.Equal(50m, result.AlignmentScore);
    }
}
