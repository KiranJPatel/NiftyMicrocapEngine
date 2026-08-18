using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.MultiTimeframe;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Decision;

public class DecisionEngineHardGateTests
{
    internal static DecisionEngine BuildEngine(DecisionEngineOptions? options = null) =>
        new(Options.Create(options ?? new DecisionEngineOptions()));

    internal static StructureSnapshot EmptyStructureSnapshot(TrendDirection trend = TrendDirection.Bullish) => new(
        1, Timeframe.Daily, trend,
        Array.Empty<SwingPoint>(), Array.Empty<StructureBreakEvent>(), Array.Empty<PriceLeg>(),
        Array.Empty<StructureZone>(), Array.Empty<SmcEvent>());

    internal static CandlePsychologyMetrics NeutralPsychology() => new(50m, 25m, 25m, 1.2m, 1.0m, 0.5m);

    internal static DecisionEngineInput BuildInput(
        TrendDirection proposedDirection = TrendDirection.Bullish,
        bool dataQualityPassed = true,
        bool circuitLocked = false,
        bool regimeSuppressed = false,
        decimal regimeOverrideConfidence = 90m,
        bool structureBreakAgainst = false,
        StructureSnapshot? snapshot = null,
        IReadOnlyDictionary<string, decimal?>? indicatorValues = null)
    {
        return new DecisionEngineInput(
            SymbolId: 1,
            AsOfDate: new DateOnly(2026, 1, 15),
            ProposedDirection: proposedDirection,
            PrimaryStructureSnapshot: snapshot ?? EmptyStructureSnapshot(proposedDirection),
            IndicatorValues: indicatorValues ?? new Dictionary<string, decimal?>(),
            MtfAlignment: new MtfAlignmentResult(80m, new Dictionary<Timeframe, TrendDirection>(), Array.Empty<Timeframe>(), false),
            RegimeResult: new RegimeFilterResult(regimeSuppressed, regimeOverrideConfidence, regimeSuppressed ? "Regime suppressed." : "OK"),
            RelativeStrength: new RelativeStrengthResult(1.2m, 1.1m, 1.3m, 1.2m),
            CandlePsychology: NeutralPsychology(),
            CandlePatterns: Array.Empty<CandlePatternMatch>(),
            DataQualityPassed: dataQualityPassed,
            DataQualityFailureReason: dataQualityPassed ? null : "Simulated data quality failure.",
            IsCircuitLockedAgainstDirection: circuitLocked,
            HasStructureBreakAgainstDirectionWithinLookback: structureBreakAgainst,
            StructureBreakAgainstDirectionDetail: structureBreakAgainst ? "Simulated structure break against direction." : null);
    }

    private static Dictionary<string, decimal?> StrongIndicators() => new()
    {
        ["EMA_20"] = 110m, ["EMA_50"] = 100m, ["ADX_14"] = 30m,
        ["RSI_14"] = 65m, ["MACD_12_26_9"] = 2m, ["Stochastic_14_3"] = 55m,
        ["OBV"] = 5000m, ["HistVol_20"] = 25m
    };

    [Fact]
    public void Evaluate_DataQualityGateFailed_ForcesNoTradeRegardlessOfScore()
    {
        var engine = BuildEngine();
        var input = BuildInput(dataQualityPassed: false, indicatorValues: StrongIndicators());

        var result = engine.Evaluate(input);

        Assert.Equal(DecisionOutcome.NoTrade, result.Outcome);
        Assert.Equal(HardGateKind.DataQuality, result.HardGateFailed);
    }

    [Fact]
    public void Evaluate_CircuitLocked_ForcesNoTrade()
    {
        var engine = BuildEngine();
        var input = BuildInput(circuitLocked: true);

        var result = engine.Evaluate(input);

        Assert.Equal(DecisionOutcome.NoTrade, result.Outcome);
        Assert.Equal(HardGateKind.CircuitLocked, result.HardGateFailed);
    }

    [Fact]
    public void Evaluate_StructureBreakAgainstDirection_ForcesNoTrade()
    {
        var engine = BuildEngine();
        var input = BuildInput(structureBreakAgainst: true);

        var result = engine.Evaluate(input);

        Assert.Equal(DecisionOutcome.NoTrade, result.Outcome);
        Assert.Equal(HardGateKind.StructureBreakAgainstDirection, result.HardGateFailed);
    }

    [Fact]
    public void Evaluate_RegimeSuppressed_BelowOverrideThreshold_ForcesNoTrade()
    {
        var engine = BuildEngine();
        var input = BuildInput(regimeSuppressed: true, regimeOverrideConfidence: 90m);

        var result = engine.Evaluate(input);

        Assert.Equal(DecisionOutcome.NoTrade, result.Outcome);
        Assert.Equal(HardGateKind.RegimeSuppressed, result.HardGateFailed);
    }

    [Fact]
    public void Evaluate_RegimeSuppressed_AtOrAboveOverrideThreshold_IsAllowedThrough()
    {
        // The critical audited-fix regression test: an extremely strong setup
        // under regime suppression must be allowed through ONLY if confidence
        // clears the override threshold — checked BEFORE mapping to outcome,
        // not as a downweight applied after.
        var engine = BuildEngine(new DecisionEngineOptions { RegimeOverrideConfidence = 1m });
        var input = BuildInput(regimeSuppressed: true, regimeOverrideConfidence: 1m, indicatorValues: StrongIndicators());

        var result = engine.Evaluate(input);

        Assert.NotEqual(DecisionOutcome.NoTrade, result.Outcome);
        Assert.Null(result.HardGateFailed);
    }

    [Fact]
    public void Evaluate_RegimeSuppressed_JustBelowOverrideThreshold_StillForcesNoTrade()
    {
        var engine = BuildEngine(new DecisionEngineOptions { RegimeOverrideConfidence = 99.9m });
        var input = BuildInput(regimeSuppressed: true, regimeOverrideConfidence: 99.9m, indicatorValues: StrongIndicators());

        var result = engine.Evaluate(input);

        Assert.Equal(DecisionOutcome.NoTrade, result.Outcome);
        Assert.Equal(HardGateKind.RegimeSuppressed, result.HardGateFailed);
    }

    [Fact]
    public void Evaluate_MultipleGatesFailed_BothMarkedFailedInGateList()
    {
        var engine = BuildEngine();
        var input = BuildInput(dataQualityPassed: false, circuitLocked: true);

        var result = engine.Evaluate(input);

        Assert.Equal(DecisionOutcome.NoTrade, result.Outcome);
        Assert.NotNull(result.HardGateFailed);
        Assert.Contains(result.HardGates, g => g.Kind == HardGateKind.DataQuality && !g.Passed);
        Assert.Contains(result.HardGates, g => g.Kind == HardGateKind.CircuitLocked && !g.Passed);
    }

    [Fact]
    public void Evaluate_AllGatesEvaluated_EvenWhenNoneFail()
    {
        var engine = BuildEngine();
        var input = BuildInput();

        var result = engine.Evaluate(input);

        Assert.Equal(4, result.HardGates.Count);
        Assert.All(result.HardGates, g => Assert.True(g.Passed));
    }

    [Fact]
    public void Evaluate_HardGateFailed_ReasoningTextStatesGateNotScore()
    {
        var engine = BuildEngine();
        var input = BuildInput(circuitLocked: true);

        var result = engine.Evaluate(input);

        Assert.Contains("Hard gate failed", result.ReasoningText);
    }
}
