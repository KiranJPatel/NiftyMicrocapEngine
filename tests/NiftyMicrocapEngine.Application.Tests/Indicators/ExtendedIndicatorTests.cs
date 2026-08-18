using NiftyMicrocapEngine.Application.Indicators.Momentum;
using NiftyMicrocapEngine.Application.Indicators.Trend;
using NiftyMicrocapEngine.Application.Indicators.Volatility;
using NiftyMicrocapEngine.Application.Indicators.Volume;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Indicators;

/// <summary>
/// Most cases here feed a CONSTANT closing price for many bars — a
/// degenerate but exactly hand-verifiable input where every formula in this
/// file collapses to a known, dividing-evenly value (see each test's
/// comment for the arithmetic). This mirrors the existing convention
/// (TrendAndMomentumIndicatorTests.Rsi_FlatPrices_Reports50) rather than
/// inventing a new one, and avoids assertions built on approximate/rounded
/// hand-calculation that could hide a real bug behind a loose tolerance.
/// A few tests below additionally check directional response to confirm the
/// indicator isn't just returning a constant regardless of input.
/// </summary>
public class ExtendedIndicatorTests
{
    private static Candle Candle(int day, decimal close, long volume = 1000) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: close, High: close + 1, Low: close - 1, Close: close, AdjClose: close, Volume: volume);

    private static ProcessingContext Ctx() => new();

    private static void Feed(IBarProcessor indicator, IEnumerable<Candle> candles)
    {
        foreach (var c in candles) indicator.OnBarClosedAsync(c, Ctx(), default).Wait();
    }

    // ---- Trend ----

    [Fact]
    public void Wma_WeightsRecentBarsMoreThanSma()
    {
        // Closes [12,18,30], weights 1,2,3: (12*1+18*2+30*3)/6 = (12+36+90)/6 = 138/6 = 23 exactly.
        // A plain SMA of the same three would be (12+18+30)/3 = 20 — WMA's exact
        // result (23) being pulled toward the most recent bar (30) confirms the
        // weighting, not just that some average was computed.
        var wma = new WmaIndicator(period: 3);
        Feed(wma, new[] { Candle(1, 12m), Candle(2, 18m), Candle(3, 30m) });

        Assert.Equal(IndicatorHealth.OK, wma.Health);
        Assert.Equal(23m, wma.CurrentValue);
    }

    [Fact]
    public void Dema_ConstantPrice_ConvergesExactlyToThatPrice()
    {
        var dema = new DemaIndicator(period: 5);
        Feed(dema, Enumerable.Range(1, 15).Select(d => Candle(d, 100m)));

        Assert.Equal(IndicatorHealth.OK, dema.Health);
        Assert.Equal(100m, dema.CurrentValue);
    }

    [Fact]
    public void Tema_ConstantPrice_ConvergesExactlyToThatPrice()
    {
        var tema = new TemaIndicator(period: 4);
        Feed(tema, Enumerable.Range(1, 20).Select(d => Candle(d, 50m)));

        Assert.Equal(IndicatorHealth.OK, tema.Health);
        Assert.Equal(50m, tema.CurrentValue);
    }

    [Fact]
    public void Kama_ConstantPrice_StaysExactlyAtThatPrice()
    {
        var kama = new KamaIndicator(erPeriod: 10);
        Feed(kama, Enumerable.Range(1, 15).Select(d => Candle(d, 75m)));

        Assert.Equal(IndicatorHealth.OK, kama.Health);
        Assert.Equal(75m, kama.CurrentValue);
    }

    [Fact]
    public void RegressionChannel_ConstantPrice_FlatSlopeAndZeroWidthBands()
    {
        var channel = new RegressionChannelIndicator(period: 10);
        Feed(channel, Enumerable.Range(1, 10).Select(d => Candle(d, 60m)));

        Assert.Equal(IndicatorHealth.OK, channel.Health);
        Assert.Equal(60m, channel.CurrentValue);
        Assert.Equal(0m, channel.SlopePerBar);
        Assert.Equal(60m, channel.UpperBand);
        Assert.Equal(60m, channel.LowerBand);
    }

    [Fact]
    public void RegressionChannel_UptrendingPrice_HasPositiveSlope()
    {
        var channel = new RegressionChannelIndicator(period: 5);
        Feed(channel, new[] { Candle(1, 10m), Candle(2, 20m), Candle(3, 30m), Candle(4, 40m), Candle(5, 50m) });

        Assert.True(channel.SlopePerBar > 0);
    }

    [Fact]
    public void Ichimoku_ConstantPrice_AllLinesConvergeToThatPrice()
    {
        var ichimoku = new IchimokuIndicator(tenkanPeriod: 9, kijunPeriod: 26, senkouBPeriod: 52);
        Feed(ichimoku, Enumerable.Range(1, 52).Select(d => Candle(d, 200m)));

        Assert.Equal(IndicatorHealth.OK, ichimoku.Health);
        Assert.Equal(200m, ichimoku.CurrentValue); // Kijun-sen
        Assert.Equal(200m, ichimoku.Tenkan);
        Assert.Equal(200m, ichimoku.SenkouA);
        Assert.Equal(200m, ichimoku.SenkouB);
    }

    // ---- Momentum ----

    [Fact]
    public void StochasticRsi_ConstantPrice_Reports50()
    {
        var stochRsi = new StochasticRsiIndicator(rsiPeriod: 5, stochPeriod: 5, dPeriod: 3);
        Feed(stochRsi, Enumerable.Range(1, 20).Select(d => Candle(d, 80m)));

        Assert.Equal(IndicatorHealth.OK, stochRsi.Health);
        Assert.Equal(50m, stochRsi.CurrentValue);
        Assert.Equal(50m, stochRsi.PercentD);
    }

    [Fact]
    public void Cci_ConstantPrice_ReportsZero()
    {
        // MeanAbsoluteDeviation is 0 for constant TypicalPrice -> the
        // indicator's explicit 0/0 guard returns 0 rather than dividing.
        var cci = new CciIndicator(period: 10);
        Feed(cci, Enumerable.Range(1, 10).Select(d => Candle(d, 90m)));

        Assert.Equal(IndicatorHealth.OK, cci.Health);
        Assert.Equal(0m, cci.CurrentValue);
    }

    [Fact]
    public void Roc_ConstantPrice_ReportsZero()
    {
        var roc = new RocIndicator(period: 5);
        Feed(roc, Enumerable.Range(1, 6).Select(d => Candle(d, 45m)));

        Assert.Equal(IndicatorHealth.OK, roc.Health);
        Assert.Equal(0m, roc.CurrentValue);
    }

    [Fact]
    public void Roc_RisingPrice_IsPositive()
    {
        var roc = new RocIndicator(period: 3);
        Feed(roc, new[] { Candle(1, 100m), Candle(2, 105m), Candle(3, 110m), Candle(4, 120m) });

        Assert.True(roc.CurrentValue > 0);
    }

    [Fact]
    public void WilliamsR_ConstantPrice_ReportsNegative50()
    {
        // High = close+1, Low = close-1 in this file's Candle helper, so even
        // a "constant close" series has a non-zero High/Low range: %R =
        // (highestHigh - close) / range * -100 = (1 / 2) * -100 = -50 exactly.
        var williamsR = new WilliamsRIndicator(period: 10);
        Feed(williamsR, Enumerable.Range(1, 10).Select(d => Candle(d, 55m)));

        Assert.Equal(IndicatorHealth.OK, williamsR.Health);
        Assert.Equal(-50m, williamsR.CurrentValue);
    }

    [Fact]
    public void Trix_ConstantPrice_ConvergesToZero()
    {
        var trix = new TrixIndicator(period: 3);
        Feed(trix, Enumerable.Range(1, 15).Select(d => Candle(d, 33m)));

        Assert.Equal(IndicatorHealth.OK, trix.Health);
        Assert.Equal(0m, trix.CurrentValue);
    }

    // ---- Volume ----

    [Fact]
    public void ChaikinMoneyFlow_ConstantPrice_ReportsZero()
    {
        // MFM = ((Close-Low)-(High-Close))/(High-Low) = ((1)-(1))/2 = 0 for
        // every bar when High=close+1, Low=close-1 -> CMF = 0 exactly.
        var cmf = new ChaikinMoneyFlowIndicator(period: 10);
        Feed(cmf, Enumerable.Range(1, 10).Select(d => Candle(d, 70m)));

        Assert.Equal(IndicatorHealth.OK, cmf.Health);
        Assert.Equal(0m, cmf.CurrentValue);
    }

    [Fact]
    public void MoneyFlowIndex_ConstantPrice_Reports50()
    {
        var mfi = new MoneyFlowIndexIndicator(period: 5);
        Feed(mfi, Enumerable.Range(1, 8).Select(d => Candle(d, 65m)));

        Assert.Equal(IndicatorHealth.OK, mfi.Health);
        Assert.Equal(50m, mfi.CurrentValue);
    }

    [Fact]
    public void MoneyFlowIndex_RisingPrice_IsAbove50()
    {
        var mfi = new MoneyFlowIndexIndicator(period: 3);
        Feed(mfi, new[] { Candle(1, 100m), Candle(2, 110m), Candle(3, 120m), Candle(4, 130m) });

        Assert.True(mfi.CurrentValue > 50m);
    }

    [Fact]
    public void VolumeEma_ConstantVolume_ConvergesExactlyToThatVolume()
    {
        var volumeEma = new VolumeEmaIndicator(period: 5);
        Feed(volumeEma, Enumerable.Range(1, 12).Select(d => Candle(d, 100m, volume: 5000)));

        Assert.Equal(IndicatorHealth.OK, volumeEma.Health);
        Assert.Equal(5000m, volumeEma.CurrentValue);
        Assert.Equal("Neutral", volumeEma.SignalState);
    }

    [Fact]
    public void VolumeEma_VolumeSpike_ReportsBullish()
    {
        var volumeEma = new VolumeEmaIndicator(period: 5);
        Feed(volumeEma, Enumerable.Range(1, 10).Select(d => Candle(d, 100m, volume: 1000)));
        volumeEma.OnBarClosedAsync(Candle(11, 100m, volume: 10000), Ctx(), default).Wait(); // far more than 1.5x the settled EMA

        Assert.Equal("Bullish", volumeEma.SignalState);
    }

    // ---- Volatility ----

    [Fact]
    public void KeltnerChannel_ConstantPrice_MiddleEqualsPriceAndBandsAreSymmetric()
    {
        // True Range settles to exactly 2 every bar (High-Low=2, and
        // |High-prevClose|=|Low-prevClose|=1 < 2), so ATR settles to 2 and
        // the bands are Middle +/- 2*ATR = Middle +/- 4 with the default multiplier.
        var keltner = new KeltnerChannelIndicator(period: 10, atrMultiplier: 2m);
        Feed(keltner, Enumerable.Range(1, 12).Select(d => Candle(d, 300m)));

        Assert.Equal(IndicatorHealth.OK, keltner.Health);
        Assert.Equal(300m, keltner.CurrentValue);
        Assert.Equal(304m, keltner.UpperBand);
        Assert.Equal(296m, keltner.LowerBand);
    }

    [Fact]
    public void RangeCompressionExpansion_ConstantRange_ReportsRatioOfOneAndNeutral()
    {
        var indicator = new RangeCompressionExpansionIndicator(period: 10);
        Feed(indicator, Enumerable.Range(1, 12).Select(d => Candle(d, 40m)));

        Assert.Equal(IndicatorHealth.OK, indicator.Health);
        Assert.Equal(1m, indicator.CurrentValue);
        Assert.Equal("Neutral", indicator.SignalState);
    }

    [Fact]
    public void RangeCompressionExpansion_SuddenWideBar_ReportsExpansion()
    {
        var indicator = new RangeCompressionExpansionIndicator(period: 10, expansionThreshold: 1.5m);
        Feed(indicator, Enumerable.Range(1, 10).Select(d => Candle(d, 40m))); // average True Range settles to 2

        var wideBar = new Candle(1, Timeframe.Daily, new DateTimeOffset(2026, 1, 11, 0, 0, 0, TimeSpan.Zero), 40m, 60m, 20m, 40m, 40m, 1000); // True Range 40, far above the 1.5x expansion threshold
        indicator.OnBarClosedAsync(wideBar, Ctx(), default).Wait();

        Assert.Equal("Expansion", indicator.SignalState);
    }
}
