using NiftyMicrocapEngine.Application.Indicators.Volatility;
using NiftyMicrocapEngine.Application.Indicators.Volume;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Indicators;

public class VolumeAndVolatilityIndicatorTests
{
    private static Candle Candle(int day, decimal close, long volume = 1000) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: close, High: close + 1, Low: close - 1, Close: close, AdjClose: close, Volume: volume);

    [Fact]
    public void Obv_RisingCloses_AccumulatesPositive()
    {
        var obv = new ObvIndicator();
        var ctx = new ProcessingContext();

        obv.OnBarClosedAsync(Candle(1, 100m, 1000), ctx, default).Wait();
        obv.OnBarClosedAsync(Candle(2, 105m, 2000), ctx, default).Wait(); // up close: +2000
        obv.OnBarClosedAsync(Candle(3, 103m, 1500), ctx, default).Wait(); // down close: -1500

        Assert.Equal(500m, obv.CurrentValue); // 0 + 2000 - 1500
    }

    [Fact]
    public void VolumeSpike_WithoutVolumeSmaInContext_ReportsInsufficientData()
    {
        var spike = new VolumeSpikeIndicator(volumeSmaPeriod: 20, spikeMultiple: 2m);
        var ctx = new ProcessingContext(); // VolumeSMA never written

        spike.OnBarClosedAsync(Candle(1, 100m, 5000), ctx, default).Wait();

        Assert.Equal(IndicatorHealth.InsufficientData, spike.Health);
    }

    [Fact]
    public void VolumeSpike_WhenVolumeExceedsMultipleOfSma_FlagsSpike()
    {
        var spike = new VolumeSpikeIndicator(volumeSmaPeriod: 20, spikeMultiple: 2m);
        var ctx = new ProcessingContext();
        ctx.Set<decimal?>("VolumeSMA_20", 1000m);

        // Simulate enough bars to clear warmup (20), all writing the same SMA context value.
        for (var d = 1; d <= 19; d++)
            spike.OnBarClosedAsync(Candle(d, 100m, 1000), ctx, default).Wait();

        spike.OnBarClosedAsync(Candle(20, 100m, 3000), ctx, default).Wait(); // 3x SMA

        Assert.Equal("VolumeSpike", spike.SignalState);
        Assert.Equal(3m, spike.SpikeRatio);
    }

    [Fact]
    public void StandardDeviation_ConstantCloses_IsZero()
    {
        var stdDev = new StandardDeviationIndicator(period: 5);
        var ctx = new ProcessingContext();
        for (var d = 1; d <= 5; d++)
            stdDev.OnBarClosedAsync(Candle(d, 100m), ctx, default).Wait();

        Assert.Equal(0m, stdDev.CurrentValue);
    }

    [Fact]
    public void BollingerBands_WithoutStdDevInContext_ReportsInsufficientData()
    {
        var bb = new BollingerBandsIndicator(period: 20, stdDevMultiple: 2m);
        var ctx = new ProcessingContext();

        bb.OnBarClosedAsync(Candle(1, 100m), ctx, default).Wait();

        Assert.Equal(IndicatorHealth.InsufficientData, bb.Health);
        Assert.Null(bb.UpperBand);
    }

    [Fact]
    public void BollingerBands_WithStdDevInContext_ComputesBandsAroundMean()
    {
        var bb = new BollingerBandsIndicator(period: 20, stdDevMultiple: 2m);
        var ctx = new ProcessingContext();
        ctx.Set<decimal?>("StdDev_20", 5m);
        ctx.Set<decimal?>("StdDev_20_Mean", 100m);

        bb.OnBarClosedAsync(Candle(1, 100m), ctx, default).Wait();

        Assert.Equal(110m, bb.UpperBand); // 100 + 2*5
        Assert.Equal(90m, bb.LowerBand);  // 100 - 2*5
        Assert.Equal(100m, bb.CurrentValue); // mid-band = mean
    }

    [Fact]
    public void HistoricalVolatility_ConstantCloses_IsZero()
    {
        var histVol = new HistoricalVolatilityIndicator(period: 5, annualizationTradingDays: 252);
        var ctx = new ProcessingContext();
        for (var d = 1; d <= 7; d++)
            histVol.OnBarClosedAsync(Candle(d, 100m), ctx, default).Wait();

        Assert.Equal(IndicatorHealth.OK, histVol.Health);
        Assert.Equal(0m, histVol.CurrentValue);
    }
}
