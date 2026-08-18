using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Volume;

/// <summary>
/// Volume Weighted Average Price, anchored to a session boundary. For Daily/Weekly
/// bars (this engine's primary timeframes — no intraday session concept applies the
/// same way), this effectively becomes a running VWAP from the start of the supplied
/// series unless ResetOnNewSession is invoked by the caller at each new trading day
/// boundary for intraday (H1/M30/M15) confirmation series. The indicator itself is
/// timeframe-agnostic; session-boundary detection is the caller's responsibility
/// (typically the pipeline driver, based on Timestamp).
/// </summary>
public sealed class VwapIndicator : IndicatorBase
{
    private decimal _cumulativeTypicalPriceVolume;
    private decimal _cumulativeVolume;

    public override string Key => "VWAP";
    public override int WarmupPeriod => 1;
    public override int Priority => 0;

    /// <summary>Call at the start of a new session (e.g. new trading day for intraday timeframes) to reset the running calculation.</summary>
    public void ResetOnNewSession()
    {
        _cumulativeTypicalPriceVolume = 0m;
        _cumulativeVolume = 0m;
    }

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var typicalPrice = (bar.High + bar.Low + bar.Close) / 3m;
        _cumulativeTypicalPriceVolume += typicalPrice * bar.Volume;
        _cumulativeVolume += bar.Volume;

        if (_cumulativeVolume == 0)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var vwap = _cumulativeTypicalPriceVolume / _cumulativeVolume;
        var signal = bar.Close > vwap ? "Bullish" : bar.Close < vwap ? "Bearish" : "Neutral";
        return new IndicatorComputation(vwap, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>
/// Rolling VWAP over a fixed trailing window (distinct from the session-anchored
/// VwapIndicator above) — a volume-weighted average price over the trailing
/// `period` bars, recomputed fresh each bar rather than accumulated since a session start.
/// </summary>
public sealed class RollingVwapIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<(decimal TypicalPrice, long Volume)> _bars;

    public RollingVwapIndicator(int period = 20)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _bars = new CircularBuffer<(decimal, long)>(period);
    }

    public override string Key => $"RollingVWAP_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var typicalPrice = (bar.High + bar.Low + bar.Close) / 3m;
        _bars.Add((typicalPrice, bar.Volume));

        if (!_bars.IsFull)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        decimal weightedSum = 0m;
        long volumeTotal = 0L;
        foreach (var (price, volume) in _bars)
        {
            weightedSum += price * volume;
            volumeTotal += volume;
        }

        if (volumeTotal == 0)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var rollingVwap = weightedSum / volumeTotal;
        var signal = bar.Close > rollingVwap ? "Bullish" : bar.Close < rollingVwap ? "Bearish" : "Neutral";
        return new IndicatorComputation(rollingVwap, signal, 1m, IndicatorHealth.OK);
    }
}
