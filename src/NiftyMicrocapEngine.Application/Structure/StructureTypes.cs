using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Structure;

public enum SwingType { High, Low }

/// <summary>A confirmed swing point — "confirmed" means the 2-bars-each-side fractal has fully formed (§8), so it's always 2 bars behind the current candle when first detected.</summary>
public sealed record SwingPoint(int SymbolId, Timeframe Timeframe, DateTimeOffset Timestamp, SwingType Type, decimal Price, bool IsBroken = false, bool IsHigherOrLower = false);

public enum TrendDirection { Bullish, Bearish, Ranging }
public enum StructureBreakKind { BOS, CHoCH }

/// <summary>A detected BOS or CHoCH event (§8) — CHoCH is specifically the first same-candle break in the direction opposite the prevailing trend.</summary>
public sealed record StructureBreakEvent(int SymbolId, Timeframe Timeframe, DateTimeOffset Timestamp, StructureBreakKind Kind, TrendDirection NewDirection, SwingPoint BrokenSwing, decimal ClosePrice);

public enum LegKind { Impulse, Correction, Trend, Range }

/// <summary>A price leg between two confirmed swing points, classified per §8's Impulse/Correction/Trend/Range rules.</summary>
public sealed record PriceLeg(int SymbolId, Timeframe Timeframe, DateTimeOffset StartTimestamp, DateTimeOffset EndTimestamp, decimal StartPrice, decimal EndPrice, LegKind Kind, TrendDirection Direction);

public enum ZoneKind { OrderBlockBullish, OrderBlockBearish, FvgBullish, FvgBearish, SupplyZone, DemandZone }
public enum ZoneStatus { Fresh, PartiallyMitigated, FullyMitigated, Invalidated }

/// <summary>A price zone from the SMC approximation layer (§9) — order block, FVG, or supply/demand zone.</summary>
public sealed record StructureZone(int SymbolId, Timeframe Timeframe, ZoneKind Kind, DateTimeOffset FormedTimestamp, decimal UpperBound, decimal LowerBound, ZoneStatus Status = ZoneStatus.Fresh, DateTimeOffset? MitigatedTimestamp = null);

public enum SmcEventKind { LiquidityGrab, BullTrap, BearTrap, FalseBreakout, FailedBreakdown, VolumeAbsorption, ExhaustionCandle, GapBreakaway, GapContinuation, GapExhaustion }

/// <summary>A detected SMC event from §9's remaining rows (traps, absorption, exhaustion, gap classification).</summary>
public sealed record SmcEvent(int SymbolId, Timeframe Timeframe, DateTimeOffset Timestamp, SmcEventKind Kind, string? Detail);

/// <summary>
/// The full structural picture for one symbol/timeframe as of the latest closed candle
/// — the primary output consumed by the Decision Engine's Structure layer (§14) and
/// the Multi-Timeframe Engine (§12).
/// </summary>
public sealed record StructureSnapshot(
    int SymbolId,
    Timeframe Timeframe,
    TrendDirection PrevailingTrend,
    IReadOnlyList<SwingPoint> RecentSwings,
    IReadOnlyList<StructureBreakEvent> RecentBreaks,
    IReadOnlyList<PriceLeg> RecentLegs,
    IReadOnlyList<StructureZone> ActiveZones,
    IReadOnlyList<SmcEvent> RecentSmcEvents);

public sealed record StructureThresholds
{
    public const string SectionName = "StructureThresholds";

    /// <summary>Bars required each side of a candle for it to confirm as a swing point (§8: "5-bar fractal", 2 each side).</summary>
    public int SwingFractalBars { get; init; } = 2;

    /// <summary>Impulse leg range multiple of ATR(14) (§8).</summary>
    public decimal ImpulseAtrMultiple { get; init; } = 1.5m;

    /// <summary>Impulse leg alternate qualifier: BOS within this many candles of the leg's start (§8/§19: ImpulseBosLookaheadCandles).</summary>
    public int ImpulseBosLookaheadCandles { get; init; } = 3;

    /// <summary>Minimum candle count for a Range/Consolidation classification (§8).</summary>
    public int RangeMinCandles { get; init; } = 10;

    /// <summary>Bull/bear trap reversal window in candles (§9/§19: TrapReversalLookaheadCandles).</summary>
    public int TrapReversalLookaheadCandles { get; init; } = 3;

    /// <summary>Volume absorption Volume threshold as a multiple of the 20-period Volume SMA (§9).</summary>
    public decimal VolumeAbsorptionMultiple { get; init; } = 2m;

    /// <summary>Volume absorption max Body% of Range (§9).</summary>
    public decimal VolumeAbsorptionMaxBodyPercent { get; init; } = 30m;

    /// <summary>Exhaustion candle range multiple of ATR(14) (§9).</summary>
    public decimal ExhaustionAtrMultiple { get; init; } = 2m;

    /// <summary>Exhaustion candle Close must fall in the outer this-percent of the range, against trend (§9).</summary>
    public decimal ExhaustionOuterRangePercent { get; init; } = 20m;

    /// <summary>Gap-exhaustion: minimum prior same-direction trend legs before a late-trend gap qualifies (§9).</summary>
    public int GapExhaustionMinPriorTrendLegs { get; init; } = 3;

    /// <summary>Gap-exhaustion reversal window in candles (§9/§19: ExhaustionGapReversalLookaheadCandles).</summary>
    public int ExhaustionGapReversalLookaheadCandles { get; init; } = 5;

    public int VolumeSmaPeriodForAbsorption { get; init; } = 20;
    public int AtrPeriod { get; init; } = 14;
}
