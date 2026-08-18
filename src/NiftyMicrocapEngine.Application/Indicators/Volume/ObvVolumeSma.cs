using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Volume;

/// <summary>
/// On-Balance Volume: cumulative running total, adding Volume on an up-close day,
/// subtracting on a down-close day, unchanged on a flat close. Unlike most other
/// indicators here, OBV never "warms up" to a value beyond the first bar — its
/// SignalState instead reflects short-term OBV slope once enough history exists.
/// </summary>
public sealed class ObvIndicator : IndicatorBase
{
    private const int SlopeLookback = 5;

    private decimal? _previousClose;
    private decimal _cumulativeObv;
    private readonly CircularBuffer<decimal> _recentObv = new(SlopeLookback);

    public override string Key => "OBV";
    public override int WarmupPeriod => 2; // needs at least one prior close to start accumulating
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        if (_previousClose is null)
        {
            _previousClose = bar.Close;
            _cumulativeObv = 0m;
            _recentObv.Add(_cumulativeObv);
            return new IndicatorComputation(_cumulativeObv, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        if (bar.Close > _previousClose.Value) _cumulativeObv += bar.Volume;
        else if (bar.Close < _previousClose.Value) _cumulativeObv -= bar.Volume;
        // flat close: unchanged

        _previousClose = bar.Close;
        _recentObv.Add(_cumulativeObv);

        var health = _recentObv.IsFull ? IndicatorHealth.OK : IndicatorHealth.InsufficientData;
        var signal = "Neutral";

        if (_recentObv.IsFull)
        {
            var oldest = _recentObv[_recentObv.Count - 1];
            signal = _cumulativeObv > oldest ? "Bullish" : _cumulativeObv < oldest ? "Bearish" : "Neutral";
        }

        return new IndicatorComputation(_cumulativeObv, signal, health == IndicatorHealth.OK ? 1m : 0m, health);
    }
}

/// <summary>Simple moving average of Volume — used both standalone and as the baseline for VolumeSpikeIndicator.</summary>
public sealed class VolumeSmaIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<long> _volumes;

    public VolumeSmaIndicator(int period = 20)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _volumes = new CircularBuffer<long>(period);
    }

    public override string Key => $"VolumeSMA_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => -50; // VolumeSpikeIndicator depends on this via context

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _volumes.Add(bar.Volume);

        if (!_volumes.IsFull)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var avg = (decimal)_volumes.Average();
        var signal = bar.Volume > avg ? "AboveAverage" : bar.Volume < avg ? "BelowAverage" : "Neutral";
        return new IndicatorComputation(avg, signal, 1m, IndicatorHealth.OK);
    }
}
