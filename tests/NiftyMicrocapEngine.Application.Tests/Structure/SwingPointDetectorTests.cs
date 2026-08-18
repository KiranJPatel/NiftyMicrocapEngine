using NiftyMicrocapEngine.Application;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Structure;

public class SwingPointDetectorTests
{
    private static Candle Candle(int day, decimal high, decimal low, decimal close) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: close, High: high, Low: low, Close: close, AdjClose: close, Volume: 1000);

    private static async Task<SwingPointDetector> RunAsync(IEnumerable<Candle> candles)
    {
        var detector = new SwingPointDetector(1, Timeframe.Daily, new StructureThresholds());
        var ctx = new ProcessingContext();
        foreach (var c in candles)
            await detector.OnBarClosedAsync(c, ctx, default);
        return detector;
    }

    [Fact]
    public async Task Detects_SwingHigh_WhenCenterBarIsMaxOfFiveBarWindow()
    {
        // Center bar (day 3) has the highest High of the 5-bar window (days 1-5).
        var candles = new[]
        {
            Candle(1, 100, 95, 98),
            Candle(2, 102, 96, 100),
            Candle(3, 110, 97, 105), // candidate swing high
            Candle(4, 103, 96, 99),
            Candle(5, 101, 95, 97)
        };

        var detector = await RunAsync(candles);

        var swingHigh = Assert.Single(detector.ConfirmedSwings);
        Assert.Equal(SwingType.High, swingHigh.Type);
        Assert.Equal(110m, swingHigh.Price);
        Assert.Equal(candles[2].Timestamp, swingHigh.Timestamp);
    }

    [Fact]
    public async Task Detects_SwingLow_WhenCenterBarIsMinOfFiveBarWindow()
    {
        var candles = new[]
        {
            Candle(1, 105, 95, 100),
            Candle(2, 104, 94, 99),
            Candle(3, 103, 85, 90), // candidate swing low
            Candle(4, 104, 93, 98),
            Candle(5, 105, 92, 97)
        };

        var detector = await RunAsync(candles);

        var swingLow = Assert.Single(detector.ConfirmedSwings);
        Assert.Equal(SwingType.Low, swingLow.Type);
        Assert.Equal(85m, swingLow.Price);
    }

    [Fact]
    public async Task NoSwing_WhenCenterBarIsNotExtremeOfWindow()
    {
        var candles = new[]
        {
            Candle(1, 110, 95, 100), // this is the actual highest — center bar below isn't extreme
            Candle(2, 102, 96, 100),
            Candle(3, 105, 97, 101), // candidate — NOT the max (day 1's High=110 is higher)
            Candle(4, 103, 96, 99),
            Candle(5, 101, 95, 97)
        };

        var detector = await RunAsync(candles);

        Assert.Empty(detector.ConfirmedSwings);
    }

    [Fact]
    public async Task Swing_NotConfirmed_UntilTrailingBarsClose()
    {
        // Only 4 candles — the 5th (confirming) bar hasn't closed yet, so no swing
        // should be reported even though day 3 looks like a local max so far.
        var candles = new[]
        {
            Candle(1, 100, 95, 98),
            Candle(2, 102, 96, 100),
            Candle(3, 110, 97, 105),
            Candle(4, 103, 96, 99)
        };

        var detector = await RunAsync(candles);

        Assert.Empty(detector.ConfirmedSwings);
    }

    [Fact]
    public async Task IsHigherOrLower_TrueWhenNewSwingExceedsPriorSameTypeSwing()
    {
        var candles = new List<Candle>
        {
            Candle(1, 100, 95, 98),
            Candle(2, 102, 96, 100),
            Candle(3, 110, 97, 105), // first swing high: 110
            Candle(4, 103, 96, 99),
            Candle(5, 101, 95, 97),
            Candle(6, 108, 100, 104),
            Candle(7, 120, 101, 115), // second swing high: 120 > 110 -> higher high
            Candle(8, 109, 100, 103),
            Candle(9, 107, 98, 101)
        };

        var detector = await RunAsync(candles);

        var swingHighs = detector.ConfirmedSwings.Where(s => s.Type == SwingType.High).ToList();
        Assert.Equal(2, swingHighs.Count);
        Assert.False(swingHighs[0].IsHigherOrLower);
        Assert.True(swingHighs[1].IsHigherOrLower);
    }
}
