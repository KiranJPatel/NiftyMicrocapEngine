using NiftyMicrocapEngine.Application;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Structure;

public class SmcEventDetectorTests
{
    private static Candle Candle(int day, decimal open, decimal high, decimal low, decimal close, long volume = 1000) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: open, High: high, Low: low, Close: close, AdjClose: close, Volume: volume);

    private static SwingPoint Swing(SwingType type, decimal price, bool isBroken = false) =>
        new(1, Timeframe.Daily, DateTimeOffset.UtcNow, type, price, isBroken);

    [Fact]
    public async Task LiquidityGrab_WickAboveSwingHigh_ClosesBackInside()
    {
        var detector = new SmcEventDetector(1, Timeframe.Daily, new StructureThresholds());
        var ctx = new ProcessingContext();
        ctx.Set<IReadOnlyList<SwingPoint>>("Structure.AllSwings", new[] { Swing(SwingType.High, 100m) });

        // Wick to 105 (above 100) but closes at 98 (back inside).
        await detector.OnBarClosedAsync(Candle(1, 97, 105, 96, 98), ctx, default);

        Assert.Contains(detector.Events, e => e.Kind == SmcEventKind.LiquidityGrab);
    }

    [Fact]
    public async Task LiquidityGrab_NotFlagged_WhenCloseExceedsSwing()
    {
        // If Close also exceeds the swing, that's a BOS, not a liquidity grab.
        var detector = new SmcEventDetector(1, Timeframe.Daily, new StructureThresholds());
        var ctx = new ProcessingContext();
        ctx.Set<IReadOnlyList<SwingPoint>>("Structure.AllSwings", new[] { Swing(SwingType.High, 100m) });

        await detector.OnBarClosedAsync(Candle(1, 97, 105, 96, 102), ctx, default);

        Assert.DoesNotContain(detector.Events, e => e.Kind == SmcEventKind.LiquidityGrab);
    }

    [Fact]
    public async Task BullTrap_DetectedWhenBosReversesWithinWindow()
    {
        var thresholds = new StructureThresholds { TrapReversalLookaheadCandles = 3 };
        var detector = new SmcEventDetector(1, Timeframe.Daily, thresholds);
        var ctx = new ProcessingContext();

        var brokenSwing = Swing(SwingType.High, 100m);
        var bosEvent = new StructureBreakEvent(1, Timeframe.Daily, DateTimeOffset.UtcNow, StructureBreakKind.BOS, TrendDirection.Bullish, brokenSwing, 101m);

        ctx.Set<StructureBreakEvent?>("Structure.NewBreak", bosEvent);
        await detector.OnBarClosedAsync(Candle(1, 100, 102, 99, 101), ctx, default); // the BOS bar itself

        ctx.Set<StructureBreakEvent?>("Structure.NewBreak", null);
        await detector.OnBarClosedAsync(Candle(2, 101, 103, 96, 97), ctx, default); // closes back below 100 -> reversal

        Assert.Contains(detector.Events, e => e.Kind == SmcEventKind.BullTrap);
    }

    [Fact]
    public async Task BullTrap_NotFlagged_WhenReversalWindowExpires()
    {
        var thresholds = new StructureThresholds { TrapReversalLookaheadCandles = 2 };
        var detector = new SmcEventDetector(1, Timeframe.Daily, thresholds);
        var ctx = new ProcessingContext();

        var brokenSwing = Swing(SwingType.High, 100m);
        var bosEvent = new StructureBreakEvent(1, Timeframe.Daily, DateTimeOffset.UtcNow, StructureBreakKind.BOS, TrendDirection.Bullish, brokenSwing, 101m);

        ctx.Set<StructureBreakEvent?>("Structure.NewBreak", bosEvent);
        await detector.OnBarClosedAsync(Candle(1, 100, 102, 99, 101), ctx, default);

        ctx.Set<StructureBreakEvent?>("Structure.NewBreak", null);
        await detector.OnBarClosedAsync(Candle(2, 101, 103, 100, 102), ctx, default); // stays above 100
        await detector.OnBarClosedAsync(Candle(3, 102, 104, 101, 103), ctx, default); // window expires (2 candles), still above

        // A later reversal after the window closed should NOT retroactively count as a trap.
        await detector.OnBarClosedAsync(Candle(4, 103, 104, 90, 91), ctx, default);

        Assert.DoesNotContain(detector.Events, e => e.Kind == SmcEventKind.BullTrap);
    }

    [Fact]
    public async Task VolumeAbsorption_FlaggedAtMarkedSwingWithHighVolumeSmallBody()
    {
        var thresholds = new StructureThresholds { VolumeAbsorptionMultiple = 2m, VolumeAbsorptionMaxBodyPercent = 30m, VolumeSmaPeriodForAbsorption = 20 };
        var detector = new SmcEventDetector(1, Timeframe.Daily, thresholds);
        var ctx = new ProcessingContext();

        ctx.Set<decimal?>("VolumeSMA_20", 1000m);
        ctx.Set<IReadOnlyList<SwingPoint>>("Structure.AllSwings", new[] { Swing(SwingType.High, 101m) });

        // Range = 10 (95-105), Body = |100-99|=1 -> 10% < 30%; Volume 2500 = 2.5x SMA(1000); swing at 101 is within [95,105].
        await detector.OnBarClosedAsync(Candle(1, 99, 105, 95, 100, volume: 2500), ctx, default);

        Assert.Contains(detector.Events, e => e.Kind == SmcEventKind.VolumeAbsorption);
    }

    [Fact]
    public async Task VolumeAbsorption_NotFlagged_WhenNotAtMarkedLevel()
    {
        var thresholds = new StructureThresholds { VolumeAbsorptionMultiple = 2m, VolumeAbsorptionMaxBodyPercent = 30m };
        var detector = new SmcEventDetector(1, Timeframe.Daily, thresholds);
        var ctx = new ProcessingContext();

        ctx.Set<decimal?>("VolumeSMA_20", 1000m);
        ctx.Set<IReadOnlyList<SwingPoint>>("Structure.AllSwings", new[] { Swing(SwingType.High, 500m) }); // far from this candle's range

        await detector.OnBarClosedAsync(Candle(1, 99, 105, 95, 100, volume: 2500), ctx, default);

        Assert.DoesNotContain(detector.Events, e => e.Kind == SmcEventKind.VolumeAbsorption);
    }

    [Fact]
    public async Task ExhaustionCandle_FlaggedWhenLargeRangeAndCloseRejectsUptrend()
    {
        var thresholds = new StructureThresholds { ExhaustionAtrMultiple = 2m, ExhaustionOuterRangePercent = 20m, AtrPeriod = 14 };
        var detector = new SmcEventDetector(1, Timeframe.Daily, thresholds);
        var ctx = new ProcessingContext();

        ctx.Set<decimal?>("ATR_14", 5m);
        ctx.Set<TrendDirection>("Structure.PrevailingTrend", TrendDirection.Bullish);

        // Range = 12 (>= 2*5=10). Close near the Low (outer 20% of range from the bottom) -> rejection in an uptrend.
        await detector.OnBarClosedAsync(Candle(1, 108, 112, 100, 101), ctx, default);

        Assert.Contains(detector.Events, e => e.Kind == SmcEventKind.ExhaustionCandle);
    }

    [Fact]
    public async Task ExhaustionCandle_NotFlagged_WhenRangeBelowAtrThreshold()
    {
        var thresholds = new StructureThresholds { ExhaustionAtrMultiple = 2m, AtrPeriod = 14 };
        var detector = new SmcEventDetector(1, Timeframe.Daily, thresholds);
        var ctx = new ProcessingContext();

        ctx.Set<decimal?>("ATR_14", 5m);
        ctx.Set<TrendDirection>("Structure.PrevailingTrend", TrendDirection.Bullish);

        // Range = 3, well below 2*5=10.
        await detector.OnBarClosedAsync(Candle(1, 100, 101, 98, 98.5m), ctx, default);

        Assert.DoesNotContain(detector.Events, e => e.Kind == SmcEventKind.ExhaustionCandle);
    }

    [Fact]
    public async Task GapContinuation_FlaggedWhenGapMatchesTrendMidRange()
    {
        var detector = new SmcEventDetector(1, Timeframe.Daily, new StructureThresholds());
        var ctx = new ProcessingContext();
        ctx.Set<TrendDirection>("Structure.PrevailingTrend", TrendDirection.Bullish);
        ctx.Set<bool>("Structure.IsRanging", false);

        await detector.OnBarClosedAsync(Candle(1, 100, 102, 99, 101), ctx, default);

        // Gap up from prior close 101 to open 108 — matches bullish trend, not ranging.
        await detector.OnBarClosedAsync(Candle(2, 108, 110, 107, 109), ctx, default);

        Assert.Contains(detector.Events, e => e.Kind == SmcEventKind.GapContinuation);
    }

    [Fact]
    public async Task GapBreakaway_FlaggedWhenGapAtImpulseLegStartBreakingRange()
    {
        var detector = new SmcEventDetector(1, Timeframe.Daily, new StructureThresholds());
        var ctx = new ProcessingContext();
        ctx.Set<TrendDirection>("Structure.PrevailingTrend", TrendDirection.Bullish);
        ctx.Set<bool>("Structure.IsRanging", true);

        var priorCandle = Candle(1, 100, 102, 99, 101);
        await detector.OnBarClosedAsync(priorCandle, ctx, default);

        var impulseLeg = new PriceLeg(1, Timeframe.Daily, priorCandle.Timestamp, DateTimeOffset.UtcNow, 101m, 120m, LegKind.Impulse, TrendDirection.Bullish);
        ctx.Set<PriceLeg?>("Structure.CompletedLeg", impulseLeg);

        await detector.OnBarClosedAsync(Candle(2, 108, 112, 107, 111), ctx, default); // gap up, isRanging=true, impulse leg completed

        Assert.Contains(detector.Events, e => e.Kind == SmcEventKind.GapBreakaway);
    }
}
