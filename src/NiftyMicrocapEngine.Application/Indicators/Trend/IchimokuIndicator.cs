using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Trend;

/// <summary>
/// Ichimoku Kinko Hyo. Standard periods: Tenkan (conversion) 9, Kijun (base) 26,
/// Senkou B (leading span B) 52.
///
/// SIMPLIFICATION, documented rather than silently approximated: classic
/// Ichimoku plots Senkou Span A/B and Chikou Span shifted 26 bars forward/back
/// respectively, forming the visual "cloud" ahead of and the lagging line
/// behind current price. This bar-sequential IBarProcessor pipeline (§3.2)
/// processes one closed bar at a time with no forward-looking buffer, so
/// there is no bar 26 periods in the future to plot Senkou Span A/B onto yet
/// — this indicator reports Senkou Span A/B computed AS OF the current bar
/// (i.e., what they will resolve to once shifted forward), not what
/// "TODAY's cloud" (computed 26 bars ago) is at the current bar. A charting
/// UI wanting the classic shifted display needs to apply the 26-bar shift
/// itself when rendering HistoricalValues/SenkouA/SenkouB history — this
/// indicator's job is providing the correct underlying values, not the
/// display transform.
///
/// CurrentValue reports Kijun-sen (the base line — the single most
/// decision-relevant anchor, analogous to a slow moving average). Tenkan,
/// SenkouA, SenkouB, and Chikou are exposed as separate public properties,
/// same pattern as StochasticIndicator exposing PercentD alongside %K.
/// </summary>
public sealed class IchimokuIndicator : IndicatorBase
{
    private readonly int _tenkanPeriod, _kijunPeriod, _senkouBPeriod;
    private readonly CircularBuffer<decimal> _highs, _lows;

    public IchimokuIndicator(int tenkanPeriod = 9, int kijunPeriod = 26, int senkouBPeriod = 52)
    {
        if (tenkanPeriod <= 0 || kijunPeriod <= 0 || senkouBPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(tenkanPeriod));
        _tenkanPeriod = tenkanPeriod;
        _kijunPeriod = kijunPeriod;
        _senkouBPeriod = senkouBPeriod;
        // One shared rolling window sized to the longest period; shorter lines read a sub-window off the same buffer.
        _highs = new CircularBuffer<decimal>(senkouBPeriod);
        _lows = new CircularBuffer<decimal>(senkouBPeriod);
    }

    public decimal? Tenkan { get; private set; }
    public decimal? SenkouA { get; private set; }
    public decimal? SenkouB { get; private set; }
    /// <summary>Chikou Span is simply Close plotted 26 bars back — no separate computation needed; expose the raw close for a charting UI to shift.</summary>
    public decimal? ChikouRawClose { get; private set; }

    public override string Key => $"Ichimoku_{_tenkanPeriod}_{_kijunPeriod}_{_senkouBPeriod}";
    public override int WarmupPeriod => _senkouBPeriod;
    public override int Priority => 0;

    private static (decimal High, decimal Low) HighLowOverLastN(CircularBuffer<decimal> highs, CircularBuffer<decimal> lows, int n)
    {
        decimal high = decimal.MinValue, low = decimal.MaxValue;
        for (var i = 0; i < n && i < highs.Count; i++)
        {
            if (highs[i] > high) high = highs[i];
            if (lows[i] < low) low = lows[i];
        }
        return (high, low);
    }

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _highs.Add(bar.High);
        _lows.Add(bar.Low);
        ChikouRawClose = bar.Close;

        if (_highs.Count >= _tenkanPeriod)
        {
            var (h, l) = HighLowOverLastN(_highs, _lows, _tenkanPeriod);
            Tenkan = (h + l) / 2m;
        }
        else
        {
            Tenkan = null;
        }

        decimal? kijun = null;
        if (_highs.Count >= _kijunPeriod)
        {
            var (h, l) = HighLowOverLastN(_highs, _lows, _kijunPeriod);
            kijun = (h + l) / 2m;
        }

        SenkouA = (Tenkan is not null && kijun is not null) ? (Tenkan.Value + kijun.Value) / 2m : null;

        if (_highs.IsFull) // full senkouBPeriod window
        {
            var (h, l) = HighLowOverLastN(_highs, _lows, _senkouBPeriod);
            SenkouB = (h + l) / 2m;
        }
        else
        {
            SenkouB = null;
        }

        if (kijun is null)
        {
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var signal = bar.Close > kijun ? "Bullish" : bar.Close < kijun ? "Bearish" : "Neutral";
        var health = _highs.IsFull ? IndicatorHealth.OK : IndicatorHealth.InsufficientData; // full cloud (SenkouB) needs the full 52-bar window
        return new IndicatorComputation(kijun, signal, _highs.IsFull ? 1m : 0.5m, health);
    }
}
