using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Volatility;

/// <summary>
/// Average True Range, Wilder-smoothed. Priority 0 (or lower than anything that
/// consumes it, e.g. SuperTrend) since several downstream indicators/structure rules
/// depend on ATR being written to the shared IProcessingContext before they run —
/// see build spec §3.2 and §8's "impulse leg" definition (range ≥ 1.5x ATR(14)).
/// </summary>
public sealed class AtrIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly List<decimal> _seedTrueRanges = new();
    private decimal? _previousAtr;
    private decimal? _previousClose;

    public AtrIndicator(int period = 14)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
    }

    public override string Key => $"ATR_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => -100; // runs before anything that consumes ATR

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var range = bar.High - bar.Low;
        var trueRange = _previousClose is null
            ? range
            : Math.Max(range, Math.Max(Math.Abs(bar.High - _previousClose.Value), Math.Abs(bar.Low - _previousClose.Value)));

        decimal? atr;
        if (_previousAtr is null)
        {
            _seedTrueRanges.Add(trueRange);
            atr = _seedTrueRanges.Count >= _period ? _seedTrueRanges.Average() : (decimal?)null;
        }
        else
        {
            atr = (_previousAtr.Value * (_period - 1) + trueRange) / _period;
        }

        _previousClose = bar.Close;
        _previousAtr = atr;

        if (atr is null)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        // ATR has no inherent directional bias — SignalState reflects volatility regime instead.
        return new IndicatorComputation(atr, "Neutral", 1m, IndicatorHealth.OK);
    }
}
