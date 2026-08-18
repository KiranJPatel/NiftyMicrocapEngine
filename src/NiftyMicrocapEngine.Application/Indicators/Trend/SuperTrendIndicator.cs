using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Trend;

/// <summary>
/// SuperTrend: ATR-banded trend-following overlay. Reads ATR from IProcessingContext
/// (written by AtrIndicator, which must run at a lower Priority in the same pipeline
/// pass) — this is the concrete example the build spec calls out for why IBarProcessor
/// exists (§3.2: "ATR feeding SuperTrend"). If ATR isn't present in the context (e.g.
/// misconfigured pipeline missing the ATR processor), this indicator reports
/// InsufficientData rather than silently computing without it.
/// </summary>
public sealed class SuperTrendIndicator : IndicatorBase
{
    private readonly string _atrContextKey;
    private readonly decimal _multiplier;

    private decimal? _previousUpperBand;
    private decimal? _previousLowerBand;
    private decimal? _previousSuperTrend;
    private bool? _previousIsUptrend;
    private int _barsSeen;

    public SuperTrendIndicator(int atrPeriod = 10, decimal multiplier = 3m)
    {
        _atrContextKey = $"ATR_{atrPeriod}";
        _multiplier = multiplier;
        AtrPeriod = atrPeriod;
    }

    public int AtrPeriod { get; }

    public override string Key => $"SuperTrend_{AtrPeriod}_{_multiplier}";
    public override int WarmupPeriod => AtrPeriod + 1;
    public override int Priority => 10; // must run after AtrIndicator (Priority -100)

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _barsSeen++;

        if (!ctx.TryGet<decimal?>(_atrContextKey, out var atrNullable) || atrNullable is not { } atr)
        {
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var mid = (bar.High + bar.Low) / 2m;
        var basicUpperBand = mid + _multiplier * atr;
        var basicLowerBand = mid - _multiplier * atr;

        var finalUpperBand = _previousUpperBand is null || bar.Close > _previousUpperBand
            ? basicUpperBand
            : Math.Min(basicUpperBand, _previousUpperBand.Value);

        var finalLowerBand = _previousLowerBand is null || bar.Close < _previousLowerBand
            ? basicLowerBand
            : Math.Max(basicLowerBand, _previousLowerBand.Value);

        bool isUptrend;
        if (_previousSuperTrend is null)
        {
            isUptrend = bar.Close >= mid;
        }
        else if (_previousIsUptrend == true)
        {
            isUptrend = bar.Close >= finalLowerBand;
        }
        else
        {
            isUptrend = bar.Close > finalUpperBand;
        }

        var superTrendValue = isUptrend ? finalLowerBand : finalUpperBand;

        _previousUpperBand = finalUpperBand;
        _previousLowerBand = finalLowerBand;
        _previousSuperTrend = superTrendValue;
        _previousIsUptrend = isUptrend;

        var health = _barsSeen < WarmupPeriod ? IndicatorHealth.InsufficientData : IndicatorHealth.OK;
        var signal = isUptrend ? "Bullish" : "Bearish";

        return new IndicatorComputation(superTrendValue, signal, 1m, health);
    }
}
