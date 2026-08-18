using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Domain;
using Microsoft.Extensions.Options;

namespace NiftyMicrocapEngine.Application.MultiTimeframe;

/// <summary>
/// Implements build spec section 12 exactly: weighted alignment across the
/// Weekly/Daily/H1/M30/M15 stack, where each timeframe contributes its full
/// configured weight if its trend matches proposedDirection, zero otherwise —
/// and where unavailable timeframes are excluded from both the numerator and
/// denominator (renormalization), not counted as misaligned.
/// </summary>
public sealed class MultiTimeframeEngine : IMultiTimeframeEngine
{
    private readonly MultiTimeframeWeights _weights;

    public MultiTimeframeEngine(IOptions<MultiTimeframeOptions> options)
    {
        _weights = options.Value.Weights;
    }

    public MtfAlignmentResult Evaluate(IReadOnlyList<TimeframeSignal> signals, TrendDirection proposedDirection)
    {
        var availableSignals = signals.Where(s => s.DataAvailable).ToList();
        var unavailableTimeframes = signals.Where(s => !s.DataAvailable).Select(s => s.Timeframe).ToList();

        if (availableSignals.Count == 0)
        {
            // No usable data at all — report zero alignment rather than throwing;
            // callers (Decision Engine) are expected to already have a data-quality
            // hard gate (§14) that would have stopped evaluation before this point
            // if the primary timeframe itself were unavailable. This is a defensive
            // fallback, not the expected path.
            return new MtfAlignmentResult(0m, new Dictionary<Timeframe, TrendDirection>(), unavailableTimeframes, WasRenormalized: true);
        }

        var totalAvailableWeight = availableSignals.Sum(s => WeightFor(s.Timeframe));

        if (totalAvailableWeight <= 0)
        {
            return new MtfAlignmentResult(0m, new Dictionary<Timeframe, TrendDirection>(), unavailableTimeframes, WasRenormalized: true);
        }

        decimal alignedWeight = 0m;
        var trendsUsed = new Dictionary<Timeframe, TrendDirection>();

        foreach (var signal in availableSignals)
        {
            var weight = WeightFor(signal.Timeframe);
            // Renormalize: this timeframe's contribution is its weight AS A
            // FRACTION OF THE AVAILABLE TOTAL, not of the full nominal 100 —
            // this is what makes missing timeframes not silently understate
            // confidence (§12's explicit requirement).
            var renormalizedWeight = weight / totalAvailableWeight * 100m;

            if (signal.Trend == proposedDirection)
            {
                alignedWeight += renormalizedWeight;
            }

            trendsUsed[signal.Timeframe] = signal.Trend;
        }

        var wasRenormalized = unavailableTimeframes.Count > 0;

        return new MtfAlignmentResult(alignedWeight, trendsUsed, unavailableTimeframes, wasRenormalized);
    }

    private decimal WeightFor(Timeframe timeframe) => timeframe switch
    {
        Timeframe.Weekly => _weights.Weekly,
        Timeframe.Daily => _weights.Daily,
        Timeframe.H1 => _weights.H1,
        Timeframe.M30 => _weights.M30,
        Timeframe.M15 => _weights.M15,
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), $"Unrecognized timeframe {timeframe} in MTF stack.")
    };
}
