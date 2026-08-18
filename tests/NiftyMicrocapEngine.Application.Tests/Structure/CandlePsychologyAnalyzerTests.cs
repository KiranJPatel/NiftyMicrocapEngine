using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Structure;

public class CandlePsychologyAnalyzerTests
{
    private readonly CandlePsychologyAnalyzer _analyzer = new();

    private static Candle Candle(int day, decimal open, decimal high, decimal low, decimal close) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: open, High: high, Low: low, Close: close, AdjClose: close, Volume: 1000);

    [Fact]
    public void DetectPatterns_Doji_WhenBodyUnder10PercentOfRange()
    {
        // Range = 10 (95-105), Body = |100.2-100| = 0.2 -> 2% < 10%
        var candles = new[] { Candle(1, 100m, 105m, 95m, 100.2m) };
        var matches = _analyzer.DetectPatterns(candles);

        Assert.Contains(matches, m => m.Type == CandlePatternType.Doji);
    }

    [Fact]
    public void DetectPatterns_Marubozu_WhenBodyOver90PercentOfRange()
    {
        // Range = 10, Body = 9.5 -> 95% > 90%
        var candles = new[] { Candle(1, 100m, 109.6m, 100m, 109.5m) };
        var matches = _analyzer.DetectPatterns(candles);

        Assert.Contains(matches, m => m.Type == CandlePatternType.Marubozu);
    }

    [Fact]
    public void DetectPatterns_PinBar_LongLowerWickSmallBody()
    {
        // Range 0-100 to 10: Low=90, High=100, Open=99, Close=100 -> body=1(10%), lower wick=9(90%), upper wick=0
        var candles = new[] { Candle(1, 99m, 100m, 90m, 100m) };
        var matches = _analyzer.DetectPatterns(candles);

        Assert.Contains(matches, m => m.Type == CandlePatternType.PinBar);
    }

    [Fact]
    public void DetectPatterns_BullishEngulfing_SecondBodyContainsFirst()
    {
        var c1 = Candle(1, 105m, 106m, 99m, 100m); // bearish body [100,105]
        var c2 = Candle(2, 99m, 107m, 98m, 106m);  // bullish body [99,106] contains [100,105]

        var matches = _analyzer.DetectPatterns(new[] { c1, c2 });

        Assert.Contains(matches, m => m.Type == CandlePatternType.EngulfingBullish);
    }

    [Fact]
    public void DetectPatterns_BearishEngulfing_SecondBodyContainsFirst()
    {
        var c1 = Candle(1, 100m, 106m, 99m, 105m); // bullish body [100,105]
        var c2 = Candle(2, 106m, 107m, 98m, 99m);  // bearish body [99,106] contains [100,105]

        var matches = _analyzer.DetectPatterns(new[] { c1, c2 });

        Assert.Contains(matches, m => m.Type == CandlePatternType.EngulfingBearish);
    }

    [Fact]
    public void DetectPatterns_Harami_SecondBodyInsideFirst()
    {
        var c1 = Candle(1, 100m, 110m, 95m, 108m); // bullish body [100,108]
        var c2 = Candle(2, 106m, 107m, 101m, 102m); // bearish body [102,106] inside [100,108]

        var matches = _analyzer.DetectPatterns(new[] { c1, c2 });

        Assert.Contains(matches, m => m.Type == CandlePatternType.Harami);
    }

    [Fact]
    public void DetectPatterns_InsideBar_SecondRangeInsideFirst()
    {
        var c1 = Candle(1, 100m, 110m, 90m, 105m);
        var c2 = Candle(2, 102m, 108m, 95m, 103m); // High/Low inside c1's

        var matches = _analyzer.DetectPatterns(new[] { c1, c2 });

        Assert.Contains(matches, m => m.Type == CandlePatternType.InsideBar);
    }

    [Fact]
    public void DetectPatterns_OutsideBar_SecondRangeEngulfsFirst()
    {
        var c1 = Candle(1, 102m, 108m, 95m, 103m);
        var c2 = Candle(2, 100m, 110m, 90m, 105m); // High/Low engulfs c1's

        var matches = _analyzer.DetectPatterns(new[] { c1, c2 });

        Assert.Contains(matches, m => m.Type == CandlePatternType.OutsideBar);
    }

    [Fact]
    public void DetectPatterns_MorningStar_ThreeCandleReversalUp()
    {
        // c1: long bearish, close 90 (open 100 -> close 90, body 10 over range 10 = 100% > 60%)
        var c1 = Candle(1, 100m, 100m, 90m, 90m);
        // c2: small body gapping down below c1's close of 90
        var c2 = Candle(2, 87m, 88m, 85m, 87.5m);
        // c3: long bullish closing above c1's midpoint (95)
        var c3 = Candle(3, 88m, 98m, 88m, 97m);

        var matches = _analyzer.DetectPatterns(new[] { c1, c2, c3 });

        Assert.Contains(matches, m => m.Type == CandlePatternType.MorningStar);
    }

    [Fact]
    public void DetectPatterns_EveningStar_ThreeCandleReversalDown()
    {
        // c1: long bullish, open 90 -> close 100
        var c1 = Candle(1, 90m, 100m, 90m, 100m);
        // c2: small body gapping up above c1's close of 100
        var c2 = Candle(2, 103m, 105m, 102m, 102.5m);
        // c3: long bearish closing below c1's midpoint (95)
        var c3 = Candle(3, 102m, 102m, 92m, 93m);

        var matches = _analyzer.DetectPatterns(new[] { c1, c2, c3 });

        Assert.Contains(matches, m => m.Type == CandlePatternType.EveningStar);
    }

    [Fact]
    public void DetectPatterns_ThreeWhiteSoldiers_ThreeStrongBullishClosingHigher()
    {
        var c1 = Candle(1, 100m, 105m, 99.5m, 104.7m);
        var c2 = Candle(2, 104.8m, 110m, 104.5m, 109.7m);
        var c3 = Candle(3, 109.8m, 115m, 109.5m, 114.7m);

        var matches = _analyzer.DetectPatterns(new[] { c1, c2, c3 });

        Assert.Contains(matches, m => m.Type == CandlePatternType.ThreeWhiteSoldiers);
    }

    [Fact]
    public void DetectPatterns_ThreeBlackCrows_ThreeStrongBearishClosingLower()
    {
        var c1 = Candle(1, 114.7m, 115m, 109.5m, 109.8m);
        var c2 = Candle(2, 109.7m, 110m, 104.5m, 104.8m);
        var c3 = Candle(3, 104.7m, 105m, 99.5m, 100m);

        var matches = _analyzer.DetectPatterns(new[] { c1, c2, c3 });

        Assert.Contains(matches, m => m.Type == CandlePatternType.ThreeBlackCrows);
    }

    [Fact]
    public void ComputeMetrics_CloseLocationInRange_ZeroAtLowOneAtHigh()
    {
        var atLow = _analyzer.ComputeMetrics(new[] { Candle(1, 100m, 110m, 90m, 90m) }, null, null);
        var atHigh = _analyzer.ComputeMetrics(new[] { Candle(1, 100m, 110m, 90m, 110m) }, null, null);

        Assert.Equal(0m, atLow.CloseLocationInRange);
        Assert.Equal(1m, atHigh.CloseLocationInRange);
    }

    [Fact]
    public void ComputeMetrics_RangeExpansionVsAtr_NullWhenAtrNotProvided()
    {
        var metrics = _analyzer.ComputeMetrics(new[] { Candle(1, 100m, 110m, 90m, 105m) }, currentAtr: null, volumeSma20: null);

        Assert.Null(metrics.RangeExpansionVsAtr);
    }

    [Fact]
    public void ComputeMetrics_RangeExpansionVsAtr_ComputedWhenAtrProvided()
    {
        // Range = 20, ATR = 10 -> expansion = 2x
        var metrics = _analyzer.ComputeMetrics(new[] { Candle(1, 100m, 110m, 90m, 105m) }, currentAtr: 10m, volumeSma20: null);

        Assert.Equal(2m, metrics.RangeExpansionVsAtr);
    }

    [Fact]
    public void DetectPatterns_EmptyList_ReturnsNoMatches()
    {
        var matches = _analyzer.DetectPatterns(Array.Empty<Candle>());
        Assert.Empty(matches);
    }
}
