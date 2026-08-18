using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Momentum;

/// <summary>
/// Average Directional Index with the Directional Movement Index (+DI/-DI) it's
/// built from — Wilder's original system. CurrentValue reports ADX; PlusDi/MinusDi
/// exposed separately. All three legs (TR, +DM, -DM) use Wilder smoothing, matching
/// the same convention as ATR/RSI elsewhere in this indicator set.
/// </summary>
public sealed class AdxIndicator : IndicatorBase
{
    private readonly int _period;

    private decimal? _previousHigh;
    private decimal? _previousLow;
    private decimal? _previousClose;

    private readonly List<decimal> _seedTr = new();
    private readonly List<decimal> _seedPlusDm = new();
    private readonly List<decimal> _seedMinusDm = new();

    private decimal? _smoothedTr;
    private decimal? _smoothedPlusDm;
    private decimal? _smoothedMinusDm;

    private readonly List<decimal> _seedDx = new();
    private decimal? _adx;

    public AdxIndicator(int period = 14)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
    }

    public decimal? PlusDi { get; private set; }
    public decimal? MinusDi { get; private set; }

    public override string Key => $"ADX_{_period}";
    public override int WarmupPeriod => _period * 2; // one period to seed DI, another to seed ADX from DX values
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        if (_previousHigh is null)
        {
            _previousHigh = bar.High;
            _previousLow = bar.Low;
            _previousClose = bar.Close;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var upMove = bar.High - _previousHigh.Value;
        var downMove = _previousLow!.Value - bar.Low;

        var plusDm = upMove > downMove && upMove > 0 ? upMove : 0m;
        var minusDm = downMove > upMove && downMove > 0 ? downMove : 0m;

        var tr = Math.Max(
            bar.High - bar.Low,
            Math.Max(Math.Abs(bar.High - _previousClose!.Value), Math.Abs(bar.Low - _previousClose.Value)));

        _previousHigh = bar.High;
        _previousLow = bar.Low;
        _previousClose = bar.Close;

        decimal smoothedTr, smoothedPlusDm, smoothedMinusDm;

        if (_smoothedTr is null)
        {
            _seedTr.Add(tr);
            _seedPlusDm.Add(plusDm);
            _seedMinusDm.Add(minusDm);

            if (_seedTr.Count < _period)
                return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

            smoothedTr = _seedTr.Sum();
            smoothedPlusDm = _seedPlusDm.Sum();
            smoothedMinusDm = _seedMinusDm.Sum();
        }
        else
        {
            smoothedTr = _smoothedTr.Value - _smoothedTr.Value / _period + tr;
            smoothedPlusDm = _smoothedPlusDm!.Value - _smoothedPlusDm.Value / _period + plusDm;
            smoothedMinusDm = _smoothedMinusDm!.Value - _smoothedMinusDm.Value / _period + minusDm;
        }

        _smoothedTr = smoothedTr;
        _smoothedPlusDm = smoothedPlusDm;
        _smoothedMinusDm = smoothedMinusDm;

        var plusDi = smoothedTr == 0 ? 0m : smoothedPlusDm / smoothedTr * 100m;
        var minusDi = smoothedTr == 0 ? 0m : smoothedMinusDm / smoothedTr * 100m;

        PlusDi = plusDi;
        MinusDi = minusDi;

        var diSum = plusDi + minusDi;
        var dx = diSum == 0 ? 0m : Math.Abs(plusDi - minusDi) / diSum * 100m;

        decimal? adx;
        if (_adx is null)
        {
            _seedDx.Add(dx);
            if (_seedDx.Count < _period)
            {
                adx = null;
            }
            else
            {
                adx = _seedDx.Average();
                _adx = adx;
            }
        }
        else
        {
            adx = (_adx.Value * (_period - 1) + dx) / _period;
            _adx = adx;
        }

        if (adx is null)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var signal = adx >= 25m
            ? (plusDi > minusDi ? "Bullish" : "Bearish")
            : "Neutral"; // ADX < 25 conventionally read as "no strong trend" regardless of DI crossover

        return new IndicatorComputation(adx, signal, 1m, IndicatorHealth.OK);
    }
}
