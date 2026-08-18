using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.Configuration.Validation;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Configuration;

public class DecisionEngineOptionsValidatorTests
{
    private readonly DecisionEngineOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _validator.Validate(null, new DecisionEngineOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WeightsDoNotSumTo100_Fails()
    {
        var options = new DecisionEngineOptions
        {
            LayerWeights = new DecisionLayerWeights { Structure = 50m, Trend = 50m, Momentum = 50m, Volume = 0, Volatility = 0, Psychology = 0, SupportResistance = 0, RelativeStrengthRegime = 0 }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("must sum to 100"));
    }

    [Fact]
    public void Validate_NegativeWeight_Fails()
    {
        var options = new DecisionEngineOptions
        {
            LayerWeights = new DecisionLayerWeights { Structure = -5m, Trend = 105m, Momentum = 0, Volume = 0, Volatility = 0, Psychology = 0, SupportResistance = 0, RelativeStrengthRegime = 0 }
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("must not be negative"));
    }

    [Fact]
    public void Validate_ThresholdsOutOfOrder_Fails()
    {
        var options = new DecisionEngineOptions
        {
            Thresholds = new DecisionThresholds { StrongBuy = 50m, Buy = 65m, Watch = 50m, Hold = 35m, Sell = 20m } // StrongBuy < Buy
        };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("descending order"));
    }

    [Fact]
    public void Validate_OverrideConfidenceBelowBuyThreshold_Fails()
    {
        var options = new DecisionEngineOptions { RegimeOverrideConfidence = 10m }; // default Buy threshold is 65

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("RegimeOverrideConfidence"));
    }
}

public class MultiTimeframeOptionsValidatorTests
{
    private readonly MultiTimeframeOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _validator.Validate(null, new MultiTimeframeOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WeightsDoNotSumTo100_Fails()
    {
        var options = new MultiTimeframeOptions { Weights = new MultiTimeframeWeights { Weekly = 10, Daily = 10, H1 = 10, M30 = 10, M15 = 10 } };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }
}

public class DataQualityGateOptionsValidatorTests
{
    private readonly DataQualityGateOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _validator.Validate(null, new DataQualityGateOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_MinimumExceedsWindow_Fails()
    {
        var options = new DataQualityGateOptions { TrailingWindowDays = 10, MinimumNonZeroVolumeDays = 30, MaxConsecutiveNoTradeDays = 5 };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("no symbol could ever pass"));
    }

    [Fact]
    public void Validate_NonPositiveWindow_Fails()
    {
        var options = new DataQualityGateOptions { TrailingWindowDays = 0 };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }
}

public class RiskManagerOptionsValidatorTests
{
    private readonly RiskManagerOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _validator.Validate(null, new RiskManagerOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ZeroStopAtrMultiple_Fails()
    {
        var options = new RiskManagerOptions { StopAtrMultiple = 0m };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("StopAtrMultiple"));
    }

    [Fact]
    public void Validate_CorrelationThresholdOutOfRange_Fails()
    {
        var options = new RiskManagerOptions { CorrelationThreshold = 1.5m };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_SectorConcentrationOver100_Fails()
    {
        var options = new RiskManagerOptions { MaxSectorConcentrationPercent = 150m };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }
}

public class ReconciliationOptionsValidatorTests
{
    private readonly ReconciliationOptionsValidator _validator = new();

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        var result = _validator.Validate(null, new ReconciliationOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ToleranceOver1_Fails()
    {
        var options = new ReconciliationOptions { AdjCloseToleranceFraction = 1.5m };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_NonPositiveLookback_Fails()
    {
        var options = new ReconciliationOptions { LookbackDays = -1 };

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
    }
}
