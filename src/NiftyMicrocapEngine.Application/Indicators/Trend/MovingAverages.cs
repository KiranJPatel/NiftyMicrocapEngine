using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Trend;

/// <summary>Simple Moving Average over Close, period-configurable. Priority 0 — no upstream dependencies.</summary>
public sealed class SmaIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<decimal> _closes;

    public SmaIndicator(int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _closes = new CircularBuffer<decimal>(period);
    }

    public override string Key => $"SMA_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _closes.Add(bar.Close);

        if (!_closes.IsFull)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var sma = _closes.Average();
        var signal = bar.Close > sma ? "Bullish" : bar.Close < sma ? "Bearish" : "Neutral";
        return new IndicatorComputation(sma, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>
/// Exponential Moving Average over Close. Seeds with an SMA over the first `period`
/// closes (standard convention), then applies exponential smoothing thereafter.
/// Priority 0 — no upstream dependencies, but many other indicators (MACD, SuperTrend
/// via ATR-independent smoothing) depend on EMA's output being available first, so
/// this must run at or before Priority 1.
/// </summary>
public sealed class EmaIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly decimal _multiplier;
    private readonly List<decimal> _seedCloses = new();
    private decimal? _previousEma;

    public EmaIndicator(int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _multiplier = 2m / (period + 1);
    }

    public override string Key => $"EMA_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        decimal ema;

        if (_previousEma is null)
        {
            _seedCloses.Add(bar.Close);
            if (_seedCloses.Count < _period)
                return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

            ema = _seedCloses.Average();
        }
        else
        {
            ema = (bar.Close - _previousEma.Value) * _multiplier + _previousEma.Value;
        }

        _previousEma = ema;
        var signal = bar.Close > ema ? "Bullish" : bar.Close < ema ? "Bearish" : "Neutral";
        return new IndicatorComputation(ema, signal, 1m, IndicatorHealth.OK);
    }
}
