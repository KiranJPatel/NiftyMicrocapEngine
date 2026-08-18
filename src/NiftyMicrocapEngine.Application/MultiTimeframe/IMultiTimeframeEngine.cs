using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.MultiTimeframe;

/// <summary>
/// Per-timeframe trend/alignment input into the MTF Engine — one per stacked
/// timeframe (Weekly, Daily, H1, M30, M15). Sourced from each timeframe's
/// StructureBreakDetector.PrevailingTrend at analysis time.
/// </summary>
public sealed record TimeframeSignal(Timeframe Timeframe, TrendDirection Trend, bool DataAvailable);

/// <summary>
/// Combines the Weekly to Daily(primary) to H1 to M30 to M15 stack into a single
/// alignment score per build spec section 12. Weights default to Weekly 40 /
/// Daily 35 / H1 10 / M30 8 / M15 7 (configurable via MultiTimeframeOptions) and
/// MUST renormalize when a configured timeframe's data is unavailable for a
/// symbol or run (e.g. broker fetch failure) — remaining weights scale to sum
/// to 100%, never silently understating confidence by treating a missing
/// timeframe as "0 aligned."
/// </summary>
public interface IMultiTimeframeEngine
{
    MtfAlignmentResult Evaluate(IReadOnlyList<TimeframeSignal> signals, TrendDirection proposedDirection);
}

public sealed record MtfAlignmentResult(
    decimal AlignmentScore,
    IReadOnlyDictionary<Timeframe, TrendDirection> TrendsUsed,
    IReadOnlyList<Timeframe> UnavailableTimeframes,
    bool WasRenormalized);
