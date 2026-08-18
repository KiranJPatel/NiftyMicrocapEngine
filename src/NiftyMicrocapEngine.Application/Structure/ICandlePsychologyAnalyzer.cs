using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Structure;

public enum CandlePatternType
{
    Doji,
    Marubozu,
    PinBar,
    EngulfingBullish,
    EngulfingBearish,
    Harami,
    InsideBar,
    OutsideBar,
    MorningStar,
    EveningStar,
    ThreeWhiteSoldiers,
    ThreeBlackCrows
}

public sealed record CandlePatternMatch(CandlePatternType Type, DateTimeOffset Timestamp);

/// <summary>
/// Per-candle psychology metrics that feed the Decision Engine's "Candle Psychology"
/// layer (§14) directly, independent of whether any named pattern (below) matched.
/// Matches build spec §10's closing paragraph exactly: Body%, wick%, range expansion
/// vs ATR, relative volume vs 20-period average, close location within range.
/// </summary>
public sealed record CandlePsychologyMetrics(
    decimal BodyPercent,
    decimal UpperWickPercent,
    decimal LowerWickPercent,
    decimal? RangeExpansionVsAtr,
    decimal? RelativeVolume,
    decimal CloseLocationInRange);

/// <summary>
/// Detects the named candle patterns from build spec §10. All thresholds are the
/// documented defaults and are configurable (bound from appsettings — see the
/// CandlePsychologyThresholds options type) rather than hardcoded, per §3.5's
/// "no hardcoded thresholds in business logic" convention.
/// </summary>
public interface ICandlePsychologyAnalyzer
{
    /// <summary>
    /// Evaluates all single/multi-candle patterns ending at the most recent candle in
    /// `orderedCandles` (which must be closed candles only, oldest-to-newest — §21).
    /// Multi-candle patterns (Engulfing, Harami, stars, soldiers/crows) require enough
    /// trailing history; patterns needing more candles than are available are simply
    /// not evaluated (no match), not reported as a data-quality issue — this differs
    /// from indicator warmup because a pattern either did or didn't form, there's no
    /// partial state to flag.
    /// </summary>
    IReadOnlyList<CandlePatternMatch> DetectPatterns(IReadOnlyList<Candle> orderedCandles);

    /// <summary>Computes the always-available per-candle metrics for the most recent candle.</summary>
    CandlePsychologyMetrics ComputeMetrics(IReadOnlyList<Candle> orderedCandles, decimal? currentAtr, decimal? volumeSma20);
}

public sealed record CandlePsychologyThresholds
{
    public decimal DojiMaxBodyPercent { get; init; } = 10m;
    public decimal MarubozuMinBodyPercent { get; init; } = 90m;
    public decimal PinBarMinWickPercent { get; init; } = 66m;
    public decimal PinBarMaxBodyPercent { get; init; } = 33m;
    public decimal PinBarMaxOppositeWickPercent { get; init; } = 10m;
    public decimal ThreeSoldiersCrowsMinBodyPercent { get; init; } = 60m;
}
