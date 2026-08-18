using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Trend;

/// <summary>
/// Hull Moving Average: HMA(n) = WMA(2*WMA(n/2) - WMA(n), sqrt(n)). Reduces lag
/// versus a plain SMA/EMA of the same period while staying smooth. Implemented
/// from scratch via internal weighted-moving-average helper buffers — no external
/// TA library, per the build spec's from-scratch indicator constraint (§7).
/// </summary>
public sealed class HmaIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly int _halfPeriod;
    private readonly int _sqrtPeriod;
    private readonly CircularBuffer<decimal> _closes;
    private readonly CircularBuffer<decimal> _rawHmaValues;

    public HmaIndicator(int period)
    {
        if (period <= 1) throw new ArgumentOutOfRangeException(nameof(period), "HMA period must be > 1.");
        _period = period;
        _halfPeriod = Math.Max(1, period / 2);
        _sqrtPeriod = Math.Max(1, (int)Math.Round(Math.Sqrt(period)));
        _closes = new CircularBuffer<decimal>(period);
        _rawHmaValues = new CircularBuffer<decimal>(_sqrtPeriod);
    }

    public override string Key => $"HMA_{_period}";
    public override int WarmupPeriod => _period + _sqrtPeriod;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _closes.Add(bar.Close);

        if (!_closes.IsFull)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var wmaFull = WeightedMovingAverage(_closes, _period);
        var wmaHalf = WeightedMovingAverage(_closes, _halfPeriod);
        var rawHma = 2m * wmaHalf - wmaFull;

        _rawHmaValues.Add(rawHma);

        if (!_rawHmaValues.IsFull)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var hma = WeightedMovingAverage(_rawHmaValues, _sqrtPeriod);
        var signal = bar.Close > hma ? "Bullish" : bar.Close < hma ? "Bearish" : "Neutral";
        return new IndicatorComputation(hma, signal, 1m, IndicatorHealth.OK);
    }

    /// <summary>Weighted moving average over the most recent `windowSize` items in the buffer (linear weights, newest heaviest).</summary>
    private static decimal WeightedMovingAverage(CircularBuffer<decimal> buffer, int windowSize)
    {
        var effectiveWindow = Math.Min(windowSize, buffer.Count);
        decimal weightedSum = 0m;
        decimal weightTotal = 0m;

        for (var i = 0; i < effectiveWindow; i++)
        {
            var weight = effectiveWindow - i; // newest (index 0) gets highest weight
            weightedSum += buffer[i] * weight;
            weightTotal += weight;
        }

        return weightTotal == 0 ? 0m : weightedSum / weightTotal;
    }
}

/// <summary>
/// Volume Weighted Moving Average over the trailing `period` bars: sum(Close*Volume) / sum(Volume).
/// </summary>
public sealed class VwmaIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<(decimal Close, long Volume)> _bars;

    public VwmaIndicator(int period)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _bars = new CircularBuffer<(decimal, long)>(period);
    }

    public override string Key => $"VWMA_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _bars.Add((bar.Close, bar.Volume));

        if (!_bars.IsFull)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        decimal weightedSum = 0m;
        long volumeTotal = 0L;
        foreach (var (close, volume) in _bars)
        {
            weightedSum += close * volume;
            volumeTotal += volume;
        }

        if (volumeTotal == 0)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var vwma = weightedSum / volumeTotal;
        var signal = bar.Close > vwma ? "Bullish" : bar.Close < vwma ? "Bearish" : "Neutral";
        return new IndicatorComputation(vwma, signal, 1m, IndicatorHealth.OK);
    }
}
