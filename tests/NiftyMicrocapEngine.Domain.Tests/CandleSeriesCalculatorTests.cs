using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Domain.Tests;

public class CandleSeriesCalculatorTests
{
    private static Candle Candle(int day, decimal open, decimal high, decimal low, decimal close) => new(
        SymbolId: 1,
        Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: open, High: high, Low: low, Close: close, AdjClose: close, Volume: 1000);

    [Fact]
    public void Compute_FirstCandle_AtrIsNull()
    {
        var calculator = new CandleSeriesCalculator(atrPeriod: 14);
        var candles = new[] { Candle(1, 100, 105, 99, 102) };

        var result = calculator.Compute(candles);

        Assert.Null(result[0].Atr);
    }

    [Fact]
    public void Compute_FewerThanPeriodCandles_AtrRemainsNull()
    {
        var calculator = new CandleSeriesCalculator(atrPeriod: 14);
        var candles = Enumerable.Range(1, 10)
            .Select(d => Candle(d, 100, 105, 99, 102))
            .ToArray();

        var result = calculator.Compute(candles);

        Assert.All(result, r => Assert.Null(r.Atr));
    }

    [Fact]
    public void Compute_GoldenValue_AtrMatchesHandCalculatedWildersSmoothing()
    {
        // Hand-verified golden case: atrPeriod=3, so ATR seeds as the simple average
        // of the first 3 true ranges, then Wilder-smooths from there.
        // Candle 1 (no prior close): TR = High-Low = 10
        // Candle 2 (prior close=100): TR = max(High-Low, |High-prevClose|, |Low-prevClose|)
        // Candle 3: same rule
        // Candle 4: first smoothed value = (seedATR*(3-1) + TR4) / 3

        var calculator = new CandleSeriesCalculator(atrPeriod: 3);
        var candles = new[]
        {
            Candle(1, 100, 110, 100, 100), // TR1 = 10 (no prior close)
            Candle(2, 100, 108, 98, 105),  // prior close 100: TR2 = max(10, |108-100|=8, |98-100|=2) = 10
            Candle(3, 105, 112, 104, 110), // prior close 105: TR3 = max(8, |112-105|=7, |104-105|=1) = 8
            Candle(4, 110, 115, 109, 112)  // prior close 110: TR4 = max(6, |115-110|=5, |109-110|=1) = 6
        };

        var result = calculator.Compute(candles);

        // Seed ATR (at candle 3, once 3 TRs collected) = (10 + 10 + 8) / 3 = 9.333...
        Assert.Null(result[0].Atr);
        Assert.Null(result[1].Atr);
        Assert.NotNull(result[2].Atr);
        Assert.Equal(28m / 3m, result[2].Atr!.Value, 6);

        // Candle 4 smoothed: (9.333.. * 2 + 6) / 3
        var expectedSeed = 28m / 3m;
        var expectedSmoothed = (expectedSeed * 2 + 6m) / 3m;
        Assert.Equal(expectedSmoothed, result[3].Atr!.Value, 6);
    }

    [Fact]
    public void Compute_Gap_IsZeroForFirstCandleAndCorrectThereafter()
    {
        var calculator = new CandleSeriesCalculator();
        var candles = new[]
        {
            Candle(1, 100, 105, 99, 102),
            Candle(2, 108, 110, 107, 109) // gap up from prior close 102
        };

        var result = calculator.Compute(candles);

        Assert.Equal(0m, result[0].Gap);
        Assert.Equal(6m, result[1].Gap); // 108 - 102
    }

    [Fact]
    public void Compute_BodyAndWickPercentages_AreCorrect()
    {
        var calculator = new CandleSeriesCalculator();
        // Open 100, High 110, Low 95, Close 105 -> Range=15, Body=5, UpperWick=5, LowerWick=5
        var candles = new[] { Candle(1, 100, 110, 95, 105) };

        var result = calculator.Compute(candles);

        Assert.Equal(5m, result[0].BodySize);
        Assert.Equal(5m, result[0].UpperWick);
        Assert.Equal(5m, result[0].LowerWick);
        Assert.Equal(5m / 15m * 100m, result[0].BodyPercent, 6);
    }

    [Fact]
    public void Compute_LogReturn_NullOnFirstCandle_PopulatedThereafter()
    {
        var calculator = new CandleSeriesCalculator();
        var candles = new[]
        {
            Candle(1, 100, 105, 99, 100),
            Candle(2, 100, 106, 99, 105)
        };

        var result = calculator.Compute(candles);

        Assert.Null(result[0].LogReturn);
        Assert.NotNull(result[1].LogReturn);
        Assert.Equal((decimal)Math.Log(105.0 / 100.0), result[1].LogReturn!.Value, 6);
    }
}
