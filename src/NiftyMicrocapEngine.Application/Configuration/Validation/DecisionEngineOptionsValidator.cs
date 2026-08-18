using Microsoft.Extensions.Options;

namespace NiftyMicrocapEngine.Application.Configuration.Validation;

/// <summary>
/// Validates DecisionEngineOptions on startup (via ValidateOnStart) rather
/// than letting a misconfigured appsettings.json silently produce wrong
/// decisions at 2am. The layer weights summing to exactly 100 is not
/// cosmetic — DecisionEngine.MapConfidenceToOutcome's threshold ladder
/// (StrongBuy/Buy/Watch/Hold/Sell) is calibrated against a 0-100 confidence
/// range; if the weights sum to something else, every threshold in
/// DecisionThresholds becomes meaningless without anyone having changed them.
/// </summary>
public sealed class DecisionEngineOptionsValidator : IValidateOptions<DecisionEngineOptions>
{
    private const decimal WeightSumTolerance = 0.01m;

    public ValidateOptionsResult Validate(string? name, DecisionEngineOptions options)
    {
        var errors = new List<string>();

        var w = options.LayerWeights;
        var weightSum = w.Structure + w.Trend + w.Momentum + w.Volume + w.Volatility + w.Psychology + w.SupportResistance + w.RelativeStrengthRegime;
        if (Math.Abs(weightSum - 100m) > WeightSumTolerance)
        {
            errors.Add($"DecisionEngine:LayerWeights must sum to 100 (currently {weightSum}). Each layer's MaxPoints comes directly from these weights, and DecisionThresholds is calibrated against a 0-100 confidence range.");
        }

        foreach (var (name2, value) in new[]
        {
            ("Structure", w.Structure), ("Trend", w.Trend), ("Momentum", w.Momentum), ("Volume", w.Volume),
            ("Volatility", w.Volatility), ("Psychology", w.Psychology), ("SupportResistance", w.SupportResistance),
            ("RelativeStrengthRegime", w.RelativeStrengthRegime)
        })
        {
            if (value < 0) errors.Add($"DecisionEngine:LayerWeights:{name2} must not be negative (currently {value}).");
        }

        var t = options.Thresholds;
        if (!(t.StrongBuy >= t.Buy && t.Buy >= t.Watch && t.Watch >= t.Hold && t.Hold >= t.Sell))
        {
            errors.Add($"DecisionEngine:Thresholds must be in descending order (StrongBuy >= Buy >= Watch >= Hold >= Sell). Currently: StrongBuy={t.StrongBuy}, Buy={t.Buy}, Watch={t.Watch}, Hold={t.Hold}, Sell={t.Sell}.");
        }

        if (options.RegimeOverrideConfidence < t.Buy)
        {
            errors.Add($"DecisionEngine:RegimeOverrideConfidence ({options.RegimeOverrideConfidence}) should be >= the Buy threshold ({t.Buy}) — an override threshold below the normal Buy bar defeats the point of the regime suppression gate.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
