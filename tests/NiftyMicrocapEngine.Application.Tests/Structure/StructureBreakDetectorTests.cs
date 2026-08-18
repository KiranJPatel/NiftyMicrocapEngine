using NiftyMicrocapEngine.Application;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Structure;

public class StructureBreakDetectorTests
{
    private static Candle Candle(int day, decimal close) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: close, High: close + 0.5m, Low: close - 0.5m, Close: close, AdjClose: close, Volume: 1000);

    private static SwingPoint Swing(int day, SwingType type, decimal price) =>
        new(1, Timeframe.Daily, new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), type, price);

    [Fact]
    public async Task FirstBreak_EverObserved_IsBosNotChoch()
    {
        var detector = new StructureBreakDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        // Seed an unbroken swing high at 100.
        ctx.Set<SwingPoint?>("Structure.NewSwing", Swing(1, SwingType.High, 100m));
        await detector.OnBarClosedAsync(Candle(1, 95m), ctx, default);

        // Close beyond 100 -> first-ever break -> should be BOS, not CHoCH (no prior trend to "change" from).
        ctx.Set<SwingPoint?>("Structure.NewSwing", null);
        await detector.OnBarClosedAsync(Candle(2, 101m), ctx, default);

        var breakEvent = Assert.Single(detector.Breaks);
        Assert.Equal(StructureBreakKind.BOS, breakEvent.Kind);
        Assert.Equal(TrendDirection.Bullish, detector.PrevailingTrend);
    }

    [Fact]
    public async Task SameDirectionBreak_AfterEstablishedTrend_IsBos()
    {
        var detector = new StructureBreakDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        ctx.Set<SwingPoint?>("Structure.NewSwing", Swing(1, SwingType.High, 100m));
        await detector.OnBarClosedAsync(Candle(1, 95m), ctx, default);
        ctx.Set<SwingPoint?>("Structure.NewSwing", null);
        await detector.OnBarClosedAsync(Candle(2, 101m), ctx, default); // establishes Bullish trend via BOS

        // A second bullish swing high forms and breaks — still BOS (same direction as prevailing trend).
        ctx.Set<SwingPoint?>("Structure.NewSwing", Swing(3, SwingType.High, 105m));
        await detector.OnBarClosedAsync(Candle(3, 102m), ctx, default);
        ctx.Set<SwingPoint?>("Structure.NewSwing", null);
        await detector.OnBarClosedAsync(Candle(4, 106m), ctx, default);

        Assert.Equal(2, detector.Breaks.Count);
        Assert.All(detector.Breaks, b => Assert.Equal(StructureBreakKind.BOS, b.Kind));
    }

    [Fact]
    public async Task OppositeDirectionBreak_AfterEstablishedTrend_IsChoch()
    {
        var detector = new StructureBreakDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        // Establish Bullish trend.
        ctx.Set<SwingPoint?>("Structure.NewSwing", Swing(1, SwingType.High, 100m));
        await detector.OnBarClosedAsync(Candle(1, 95m), ctx, default);
        ctx.Set<SwingPoint?>("Structure.NewSwing", null);
        await detector.OnBarClosedAsync(Candle(2, 101m), ctx, default); // BOS, trend = Bullish

        // Now an unbroken swing low breaks — opposite direction -> CHoCH.
        ctx.Set<SwingPoint?>("Structure.NewSwing", Swing(3, SwingType.Low, 90m));
        await detector.OnBarClosedAsync(Candle(3, 95m), ctx, default);
        ctx.Set<SwingPoint?>("Structure.NewSwing", null);
        await detector.OnBarClosedAsync(Candle(4, 89m), ctx, default);

        Assert.Equal(2, detector.Breaks.Count);
        Assert.Equal(StructureBreakKind.BOS, detector.Breaks[0].Kind);
        Assert.Equal(StructureBreakKind.CHoCH, detector.Breaks[1].Kind);
        Assert.Equal(TrendDirection.Bearish, detector.PrevailingTrend);
    }

    [Fact]
    public async Task AfterChoch_NextSameDirectionBreak_IsBosAgain()
    {
        var detector = new StructureBreakDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        ctx.Set<SwingPoint?>("Structure.NewSwing", Swing(1, SwingType.High, 100m));
        await detector.OnBarClosedAsync(Candle(1, 95m), ctx, default);
        ctx.Set<SwingPoint?>("Structure.NewSwing", null);
        await detector.OnBarClosedAsync(Candle(2, 101m), ctx, default); // BOS bullish

        ctx.Set<SwingPoint?>("Structure.NewSwing", Swing(3, SwingType.Low, 90m));
        await detector.OnBarClosedAsync(Candle(3, 95m), ctx, default);
        ctx.Set<SwingPoint?>("Structure.NewSwing", null);
        await detector.OnBarClosedAsync(Candle(4, 89m), ctx, default); // CHoCH bearish

        // A new bearish swing low breaks — same direction as the now-prevailing (Bearish) trend -> BOS.
        ctx.Set<SwingPoint?>("Structure.NewSwing", Swing(5, SwingType.Low, 85m));
        await detector.OnBarClosedAsync(Candle(5, 87m), ctx, default);
        ctx.Set<SwingPoint?>("Structure.NewSwing", null);
        await detector.OnBarClosedAsync(Candle(6, 84m), ctx, default);

        Assert.Equal(3, detector.Breaks.Count);
        Assert.Equal(StructureBreakKind.BOS, detector.Breaks[0].Kind);
        Assert.Equal(StructureBreakKind.CHoCH, detector.Breaks[1].Kind);
        Assert.Equal(StructureBreakKind.BOS, detector.Breaks[2].Kind);
    }

    [Fact]
    public async Task NoBreak_WhenCloseDoesNotExceedSwing()
    {
        var detector = new StructureBreakDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        ctx.Set<SwingPoint?>("Structure.NewSwing", Swing(1, SwingType.High, 100m));
        await detector.OnBarClosedAsync(Candle(1, 95m), ctx, default);
        ctx.Set<SwingPoint?>("Structure.NewSwing", null);
        await detector.OnBarClosedAsync(Candle(2, 99m), ctx, default); // does not exceed 100

        Assert.Empty(detector.Breaks);
        Assert.Equal(TrendDirection.Ranging, detector.PrevailingTrend);
    }
}
