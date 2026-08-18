using NiftyMicrocapEngine.Application;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Structure;

public class SmcZoneDetectorTests
{
    private static Candle Candle(int day, decimal open, decimal high, decimal low, decimal close) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: open, High: high, Low: low, Close: close, AdjClose: close, Volume: 1000);

    [Fact]
    public async Task DetectsBullishFvg_WhenCandle1HighBelowCandle3Low()
    {
        var detector = new SmcZoneDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        var c1 = Candle(1, 100, 102, 99, 101);  // High = 102
        var c2 = Candle(2, 103, 108, 102, 107);
        var c3 = Candle(3, 109, 112, 105, 110); // Low = 105 > c1.High(102)

        await detector.OnBarClosedAsync(c1, ctx, default);
        await detector.OnBarClosedAsync(c2, ctx, default);
        await detector.OnBarClosedAsync(c3, ctx, default);

        var fvg = Assert.Single(detector.Zones, z => z.Kind == ZoneKind.FvgBullish);
        Assert.Equal(105m, fvg.UpperBound); // c3.Low
        Assert.Equal(102m, fvg.LowerBound); // c1.High
    }

    [Fact]
    public async Task DetectsBearishFvg_WhenCandle1LowAboveCandle3High()
    {
        var detector = new SmcZoneDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        var c1 = Candle(1, 110, 112, 108, 109);  // Low = 108
        var c2 = Candle(2, 107, 108, 102, 103);
        var c3 = Candle(3, 101, 104, 98, 99);    // High = 104 < c1.Low(108)

        await detector.OnBarClosedAsync(c1, ctx, default);
        await detector.OnBarClosedAsync(c2, ctx, default);
        await detector.OnBarClosedAsync(c3, ctx, default);

        var fvg = Assert.Single(detector.Zones, z => z.Kind == ZoneKind.FvgBearish);
        Assert.Equal(108m, fvg.UpperBound); // c1.Low
        Assert.Equal(104m, fvg.LowerBound); // c3.High
    }

    [Fact]
    public async Task NoFvg_WhenCandlesOverlap()
    {
        var detector = new SmcZoneDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        var c1 = Candle(1, 100, 105, 99, 101);
        var c2 = Candle(2, 101, 106, 100, 104);
        var c3 = Candle(3, 104, 107, 102, 105); // overlaps c1's range — no gap

        await detector.OnBarClosedAsync(c1, ctx, default);
        await detector.OnBarClosedAsync(c2, ctx, default);
        await detector.OnBarClosedAsync(c3, ctx, default);

        Assert.DoesNotContain(detector.Zones, z => z.Kind is ZoneKind.FvgBullish or ZoneKind.FvgBearish);
    }

    [Fact]
    public async Task Zone_FullyMitigated_WhenLaterCandleFullyCoversIt()
    {
        var detector = new SmcZoneDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        var c1 = Candle(1, 100, 102, 99, 101);
        var c2 = Candle(2, 103, 108, 102, 107);
        var c3 = Candle(3, 109, 112, 105, 110); // creates bullish FVG [102, 105]

        await detector.OnBarClosedAsync(c1, ctx, default);
        await detector.OnBarClosedAsync(c2, ctx, default);
        await detector.OnBarClosedAsync(c3, ctx, default);

        var fvgBeforeMitigation = detector.Zones.Single(z => z.Kind == ZoneKind.FvgBullish);
        Assert.Equal(ZoneStatus.Fresh, fvgBeforeMitigation.Status);

        // A later candle whose range fully covers [102,105] should fully mitigate it.
        var c4 = Candle(4, 106, 108, 100, 103);
        await detector.OnBarClosedAsync(c4, ctx, default);

        var fvgAfterMitigation = detector.Zones.Single(z => z.Kind == ZoneKind.FvgBullish);
        Assert.Equal(ZoneStatus.FullyMitigated, fvgAfterMitigation.Status);
    }

    [Fact]
    public async Task Zone_PartiallyMitigated_WhenLaterCandlePartiallyTradesInto()
    {
        var detector = new SmcZoneDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        var c1 = Candle(1, 100, 102, 99, 101);
        var c2 = Candle(2, 103, 108, 102, 107);
        var c3 = Candle(3, 109, 112, 105, 110); // FVG [102, 105]

        await detector.OnBarClosedAsync(c1, ctx, default);
        await detector.OnBarClosedAsync(c2, ctx, default);
        await detector.OnBarClosedAsync(c3, ctx, default);

        // Later candle trades into the zone but doesn't fully cover it (Low=103, inside the zone; High=106, above it).
        var c4 = Candle(4, 106, 106, 103, 104);
        await detector.OnBarClosedAsync(c4, ctx, default);

        var fvg = detector.Zones.Single(z => z.Kind == ZoneKind.FvgBullish);
        Assert.Equal(ZoneStatus.PartiallyMitigated, fvg.Status);
    }

    [Fact]
    public async Task OrderBlockBullish_DetectedFromLastDownCloseCandleBeforeImpulseUp()
    {
        var detector = new SmcZoneDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        var downCloseCandle = Candle(1, 100, 101, 97, 98); // down-close: Close(98) < Open(100)
        await detector.OnBarClosedAsync(downCloseCandle, ctx, default);

        var impulseEndCandle = Candle(2, 98, 115, 97, 114);
        var impulseLeg = new PriceLeg(1, Timeframe.Daily, downCloseCandle.Timestamp, impulseEndCandle.Timestamp, 98m, 114m, LegKind.Impulse, TrendDirection.Bullish);
        ctx.Set<PriceLeg?>("Structure.CompletedLeg", impulseLeg);

        await detector.OnBarClosedAsync(impulseEndCandle, ctx, default);

        Assert.Contains(detector.Zones, z => z.Kind == ZoneKind.OrderBlockBullish);
        Assert.Contains(detector.Zones, z => z.Kind == ZoneKind.SupplyZone);
    }

    [Fact]
    public async Task OrderBlockBearish_DetectedFromLastUpCloseCandleBeforeImpulseDown()
    {
        var detector = new SmcZoneDetector(1, Timeframe.Daily);
        var ctx = new ProcessingContext();

        var upCloseCandle = Candle(1, 100, 103, 99, 102); // up-close: Close(102) > Open(100)
        await detector.OnBarClosedAsync(upCloseCandle, ctx, default);

        var impulseEndCandle = Candle(2, 102, 103, 85, 86);
        var impulseLeg = new PriceLeg(1, Timeframe.Daily, upCloseCandle.Timestamp, impulseEndCandle.Timestamp, 102m, 86m, LegKind.Impulse, TrendDirection.Bearish);
        ctx.Set<PriceLeg?>("Structure.CompletedLeg", impulseLeg);

        await detector.OnBarClosedAsync(impulseEndCandle, ctx, default);

        Assert.Contains(detector.Zones, z => z.Kind == ZoneKind.OrderBlockBearish);
        Assert.Contains(detector.Zones, z => z.Kind == ZoneKind.DemandZone);
    }
}
