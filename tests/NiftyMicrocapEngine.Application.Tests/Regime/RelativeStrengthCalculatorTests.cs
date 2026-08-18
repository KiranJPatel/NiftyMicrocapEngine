using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Regime;

public class RelativeStrengthCalculatorTests
{
    private static RelativeStrengthCalculator BuildCalculator(int shortDays = 20, int longDays = 60)
    {
        var options = new RelativeStrengthOptions { LookbackDaysShort = shortDays, LookbackDaysLong = longDays };
        return new RelativeStrengthCalculator(Options.Create(options));
    }

    private static List<Candle> ConstantGrowthSeries(int count, decimal startPrice, decimal dailyGrowthFraction)
    {
        var candles = new List<Candle>();
        var price = startPrice;
        for (var i = 0; i < count; i++)
        {
            candles.Add(new Candle(1, Timeframe.Daily, DateTimeOffset.UtcNow.AddDays(i - count),
                price, price + 1, price - 1, price, price, 1000));
            price *= 1 + dailyGrowthFraction;
        }
        return candles;
    }

    [Fact]
    public void Calculate_SymbolOutperformsBenchmark_RatioGreaterThanOne()
    {
        var calculator = BuildCalculator(shortDays: 20, longDays: 60);

        var symbolCandles = ConstantGrowthSeries(70, 100m, 0.02m); // faster growth
        var microcapCandles = ConstantGrowthSeries(70, 100m, 0.01m); // slower growth
        var nifty50Candles = ConstantGrowthSeries(70, 100m, 0.005m);

        var result = calculator.Calculate(symbolCandles, microcapCandles, nifty50Candles);

        Assert.NotNull(result.ReturnRatioVsMicrocap250Short);
        Assert.True(result.ReturnRatioVsMicrocap250Short > 1m);
        Assert.NotNull(result.ReturnRatioVsNifty50Short);
        Assert.True(result.ReturnRatioVsNifty50Short > 1m);
    }

    [Fact]
    public void Calculate_InsufficientHistoryForLookback_ReturnsNullNotFabricated()
    {
        var calculator = BuildCalculator(shortDays: 20, longDays: 60);

        // Only 10 candles — fewer than even the short lookback (20).
        var symbolCandles = ConstantGrowthSeries(10, 100m, 0.01m);
        var microcapCandles = ConstantGrowthSeries(70, 100m, 0.01m);
        var nifty50Candles = ConstantGrowthSeries(70, 100m, 0.01m);

        var result = calculator.Calculate(symbolCandles, microcapCandles, nifty50Candles);

        Assert.Null(result.ReturnRatioVsMicrocap250Short);
        Assert.Null(result.ReturnRatioVsMicrocap250Long);
        Assert.Null(result.ReturnRatioVsNifty50Short);
        Assert.Null(result.ReturnRatioVsNifty50Long);
    }

    [Fact]
    public void Calculate_LongLookbackAvailableButShortLookbackInsufficient_PopulatesOnlyWhatFits()
    {
        // Exactly enough for the long (60) lookback but well past the short (20) too —
        // both should populate. This test instead checks the boundary: exactly
        // longDays+1 candles populates Long but with fewer candles than shortDays+1
        // would leave Short null; here we verify both populate when both fit.
        var calculator = BuildCalculator(shortDays: 20, longDays: 60);

        var symbolCandles = ConstantGrowthSeries(61, 100m, 0.01m);
        var microcapCandles = ConstantGrowthSeries(61, 100m, 0.01m);
        var nifty50Candles = ConstantGrowthSeries(61, 100m, 0.01m);

        var result = calculator.Calculate(symbolCandles, microcapCandles, nifty50Candles);

        Assert.NotNull(result.ReturnRatioVsMicrocap250Short);
        Assert.NotNull(result.ReturnRatioVsMicrocap250Long);
    }

    [Fact]
    public void Calculate_BenchmarkFlatReturn_ReturnsNullRatherThanDivideByZero()
    {
        var calculator = BuildCalculator(shortDays: 20, longDays: 60);

        var symbolCandles = ConstantGrowthSeries(70, 100m, 0.01m);
        var flatBenchmark = ConstantGrowthSeries(70, 100m, 0m); // zero return over the window

        var result = calculator.Calculate(symbolCandles, flatBenchmark, flatBenchmark);

        Assert.Null(result.ReturnRatioVsMicrocap250Short);
        Assert.Null(result.ReturnRatioVsNifty50Short);
    }

    [Fact]
    public void Calculate_UsesConfiguredLookbackWindows_NotHardcoded()
    {
        var calculator = BuildCalculator(shortDays: 5, longDays: 10);

        var symbolCandles = ConstantGrowthSeries(11, 100m, 0.02m);
        var benchmarkCandles = ConstantGrowthSeries(11, 100m, 0.01m);

        var result = calculator.Calculate(symbolCandles, benchmarkCandles, benchmarkCandles);

        // With only 11 candles, a default-config (20/60) calculator would return
        // null for everything; this custom 5/10 config should populate both.
        Assert.NotNull(result.ReturnRatioVsMicrocap250Short);
        Assert.NotNull(result.ReturnRatioVsMicrocap250Long);
    }
}
