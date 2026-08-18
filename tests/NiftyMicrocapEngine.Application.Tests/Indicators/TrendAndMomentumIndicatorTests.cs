using NiftyMicrocapEngine.Application.Indicators.Momentum;
using NiftyMicrocapEngine.Application.Indicators.Trend;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Indicators;

public class TrendAndMomentumIndicatorTests
{
    private static Candle Candle(int day, decimal close, decimal? open = null, long volume = 1000) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: open ?? close, High: close + 1, Low: close - 1, Close: close, AdjClose: close, Volume: volume);

    private static ProcessingContext Ctx() => new();

    [Fact]
    public void Sma_BeforeWarmup_ReportsInsufficientData()
    {
        var sma = new SmaIndicator(period: 5);
        for (var d = 1; d <= 4; d++)
            sma.OnBarClosedAsync(Candle(d, 100m), Ctx(), default).Wait();

        Assert.Equal(IndicatorHealth.InsufficientData, sma.Health);
        Assert.Null(sma.CurrentValue);
    }

    [Fact]
    public void Sma_AtWarmup_ComputesCorrectAverage()
    {
        var sma = new SmaIndicator(period: 3);
        var closes = new[] { 10m, 20m, 30m };
        foreach (var (close, i) in closes.Select((c, i) => (c, i)))
            sma.OnBarClosedAsync(Candle(i + 1, close), Ctx(), default).Wait();

        Assert.Equal(IndicatorHealth.OK, sma.Health);
        Assert.Equal(20m, sma.CurrentValue);
    }

    [Fact]
    public void Ema_SeedsWithSmaThenSmooths()
    {
        var ema = new EmaIndicator(period: 3);
        // Seed: avg(10,20,30) = 20
        ema.OnBarClosedAsync(Candle(1, 10m), Ctx(), default).Wait();
        ema.OnBarClosedAsync(Candle(2, 20m), Ctx(), default).Wait();
        ema.OnBarClosedAsync(Candle(3, 30m), Ctx(), default).Wait();
        Assert.Equal(20m, ema.CurrentValue);

        // Next: (40 - 20) * (2/4) + 20 = 30
        ema.OnBarClosedAsync(Candle(4, 40m), Ctx(), default).Wait();
        Assert.Equal(30m, ema.CurrentValue);
    }

    [Fact]
    public void Rsi_AllGains_ReportsExactly100WhenAvgLossIsZero()
    {
        var rsi = new RsiIndicator(period: 5);
        // Strictly increasing closes -> avgLoss = 0 -> RSI = 100 per the indicator's explicit zero-loss rule.
        for (var d = 1; d <= 7; d++)
            rsi.OnBarClosedAsync(Candle(d, 100m + d * 10), Ctx(), default).Wait();

        Assert.Equal(IndicatorHealth.OK, rsi.Health);
        Assert.Equal(100m, rsi.CurrentValue);
        Assert.Equal("Overbought", rsi.SignalState);
    }

    [Fact]
    public void Rsi_FlatPrices_Reports50()
    {
        var rsi = new RsiIndicator(period: 5);
        for (var d = 1; d <= 7; d++)
            rsi.OnBarClosedAsync(Candle(d, 100m), Ctx(), default).Wait();

        Assert.Equal(50m, rsi.CurrentValue);
    }

    [Fact]
    public void Macd_BeforeSlowPeriodWarmup_IsNull()
    {
        var macd = new MacdIndicator(fastPeriod: 3, slowPeriod: 6, signalPeriod: 2);
        for (var d = 1; d <= 5; d++)
            macd.OnBarClosedAsync(Candle(d, 100m + d), Ctx(), default).Wait();

        Assert.Null(macd.CurrentValue);
    }

    [Fact]
    public void SuperTrend_WithoutAtrInContext_ReportsInsufficientData()
    {
        var superTrend = new SuperTrendIndicator(atrPeriod: 10, multiplier: 3m);
        var ctx = new ProcessingContext(); // ATR never written — simulates misconfigured pipeline

        superTrend.OnBarClosedAsync(Candle(1, 100m), ctx, default).Wait();

        Assert.Equal(IndicatorHealth.InsufficientData, superTrend.Health);
        Assert.Null(superTrend.CurrentValue);
    }

    [Fact]
    public void SuperTrend_WithAtrInContext_ComputesValue()
    {
        var superTrend = new SuperTrendIndicator(atrPeriod: 10, multiplier: 3m);
        var ctx = new ProcessingContext();
        ctx.Set<decimal?>("ATR_10", 2m);

        superTrend.OnBarClosedAsync(Candle(1, 100m, open: 99m), ctx, default).Wait();

        Assert.NotNull(superTrend.CurrentValue);
    }
}
