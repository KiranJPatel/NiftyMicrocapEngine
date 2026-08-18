using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.Decision;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Decision;

public class DecisionEngineScoringTests
{
    private static Dictionary<string, decimal?> StrongIndicators() => new()
    {
        ["EMA_20"] = 110m, ["EMA_50"] = 100m, ["ADX_14"] = 30m,
        ["RSI_14"] = 65m, ["MACD_12_26_9"] = 2m, ["Stochastic_14_3"] = 55m,
        ["OBV"] = 5000m, ["HistVol_20"] = 25m
    };

    [Fact]
    public void Evaluate_LayerWeights_SumToConfiguredHundred()
    {
        var engine = DecisionEngineHardGateTests.BuildEngine();
        var input = DecisionEngineHardGateTests.BuildInput(indicatorValues: StrongIndicators());

        var result = engine.Evaluate(input);

        var maxPossible = result.LayerScores.Sum(l => l.MaxPoints);
        Assert.Equal(100m, maxPossible);
    }

    [Fact]
    public void Evaluate_AllEightLayersPresent()
    {
        var engine = DecisionEngineHardGateTests.BuildEngine();
        var input = DecisionEngineHardGateTests.BuildInput(indicatorValues: StrongIndicators());

        var result = engine.Evaluate(input);

        Assert.Equal(8, result.LayerScores.Count);
    }

    [Fact]
    public void Evaluate_ConfidenceScore_NeverExceedsBounds()
    {
        var engine = DecisionEngineHardGateTests.BuildEngine();
        var input = DecisionEngineHardGateTests.BuildInput(indicatorValues: StrongIndicators());

        var result = engine.Evaluate(input);

        Assert.InRange(result.ConfidenceScore, 0m, 100m);
    }

    [Fact]
    public void Evaluate_ReasoningText_IsNonEmpty()
    {
        var engine = DecisionEngineHardGateTests.BuildEngine();
        var input = DecisionEngineHardGateTests.BuildInput();

        var result = engine.Evaluate(input);

        Assert.False(string.IsNullOrWhiteSpace(result.ReasoningText));
    }

    [Fact]
    public void Evaluate_UsesConfiguredOutcomeThresholds_NotHardcoded()
    {
        // With an extremely permissive StrongBuy threshold, even a middling setup should hit it —
        // proves the mapping reads from config rather than a hardcoded >= 80 check.
        var options = new DecisionEngineOptions
        {
            Thresholds = new DecisionThresholds { StrongBuy = 1m, Buy = 0.5m, Watch = 0m, Hold = -50m, Sell = -100m }
        };
        var engine = DecisionEngineHardGateTests.BuildEngine(options);
        var input = DecisionEngineHardGateTests.BuildInput();

        var result = engine.Evaluate(input);

        Assert.Equal(DecisionOutcome.StrongBuy, result.Outcome);
    }

    [Fact]
    public void Evaluate_UsesConfiguredLayerWeights_NotHardcoded()
    {
        // Zero out every layer except Structure — total possible points should
        // reflect only Structure's configured weight, proving weights are read
        // from config rather than the old hardcoded per-layer constants.
        var options = new DecisionEngineOptions
        {
            LayerWeights = new DecisionLayerWeights
            {
                Structure = 100m, Trend = 0m, Momentum = 0m, Volume = 0m,
                Volatility = 0m, Psychology = 0m, SupportResistance = 0m, RelativeStrengthRegime = 0m
            }
        };
        var engine = DecisionEngineHardGateTests.BuildEngine(options);
        var input = DecisionEngineHardGateTests.BuildInput(indicatorValues: StrongIndicators());

        var result = engine.Evaluate(input);

        var maxPossible = result.LayerScores.Sum(l => l.MaxPoints);
        Assert.Equal(100m, maxPossible);

        var structureLayer = result.LayerScores.Single(l => l.LayerName == "Market Structure");
        Assert.Equal(100m, structureLayer.MaxPoints);

        var trendLayer = result.LayerScores.Single(l => l.LayerName == "Trend");
        Assert.Equal(0m, trendLayer.MaxPoints);
    }
}
