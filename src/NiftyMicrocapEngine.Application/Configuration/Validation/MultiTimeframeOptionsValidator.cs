using Microsoft.Extensions.Options;

namespace NiftyMicrocapEngine.Application.Configuration.Validation;

/// <summary>
/// Validates that MTF weights sum to 100. MultiTimeframeEngine's
/// renormalization logic (dividing by totalAvailableWeight) still produces
/// a mathematically valid 0-100 score even if the nominal weights don't sum
/// to 100 — but the whole-stack-available case then silently returns a
/// different scale than callers expect, which is exactly the kind of
/// "technically works, quietly wrong" bug config validation exists to catch
/// before it reaches production.
/// </summary>
public sealed class MultiTimeframeOptionsValidator : IValidateOptions<MultiTimeframeOptions>
{
    private const decimal WeightSumTolerance = 0.01m;

    public ValidateOptionsResult Validate(string? name, MultiTimeframeOptions options)
    {
        var w = options.Weights;
        var sum = w.Weekly + w.Daily + w.H1 + w.M30 + w.M15;

        if (Math.Abs(sum - 100m) > WeightSumTolerance)
        {
            return ValidateOptionsResult.Fail(
                $"MultiTimeframe:Weights must sum to 100 (currently {sum}). MultiTimeframeEngine renormalizes when timeframes are unavailable, but the full-stack-available case relies on the nominal weights already summing to 100.");
        }

        foreach (var (label, value) in new[] { ("Weekly", w.Weekly), ("Daily", w.Daily), ("H1", w.H1), ("M30", w.M30), ("M15", w.M15) })
        {
            if (value < 0) return ValidateOptionsResult.Fail($"MultiTimeframe:Weights:{label} must not be negative (currently {value}).");
        }

        return ValidateOptionsResult.Success;
    }
}
