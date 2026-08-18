using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Regime;

public enum BroadMarketTrendState { StrongBull, Bull, Neutral, Bear, StrongBear }

/// <summary>
/// The full Trend/Structure analysis run on a broad-market index (Nifty 50,
/// Nifty Midcap) rather than an individual microcap. BroadMarketTrendState adds
/// the Strong Bull/Strong Bear distinction the regime filter's suppression rule
/// specifically keys off.
/// </summary>
public sealed record BroadMarketState(BroadMarketTrendState Nifty50Trend, BroadMarketTrendState NiftyMidcapTrend, DateOnly AsOfDate);

/// <summary>
/// Result of applying the regime filter to one candidate setup. This is a
/// Decision Engine hard-gate INPUT, not merely informational — a Suppressed
/// result must short-circuit the Decision Engine to No Trade unless the setup's
/// own score clears the override threshold (the audited fix requiring a
/// short-circuit rather than a downweight).
/// </summary>
public sealed record RegimeFilterResult(bool IsSuppressed, decimal RequiredOverrideConfidence, string Reason);

/// <summary>
/// Runs ahead of individual microcap scoring; tightens the Buy/Strong Buy
/// threshold, or outright suppresses new longs, during confirmed broad-market
/// weakness (Nifty 50 trend = Bear or StrongBear).
/// </summary>
public interface IRegimeFilter
{
    RegimeFilterResult Evaluate(BroadMarketState marketState, TrendDirection proposedDirection);
}

/// <summary>
/// Return-ratio of a microcap vs the Nifty Microcap 250 index and vs Nifty 50,
/// over configurable lookback windows (default 20 and 60 trading days). Feeds
/// the Decision Engine's "Relative Strength & Regime alignment" scoring layer.
/// </summary>
public interface IRelativeStrengthCalculator
{
    RelativeStrengthResult Calculate(
        IReadOnlyList<Candle> symbolCandles,
        IReadOnlyList<Candle> niftyMicrocap250Candles,
        IReadOnlyList<Candle> nifty50Candles);
}

public sealed record RelativeStrengthResult(
    decimal? ReturnRatioVsMicrocap250Short,
    decimal? ReturnRatioVsMicrocap250Long,
    decimal? ReturnRatioVsNifty50Short,
    decimal? ReturnRatioVsNifty50Long);
