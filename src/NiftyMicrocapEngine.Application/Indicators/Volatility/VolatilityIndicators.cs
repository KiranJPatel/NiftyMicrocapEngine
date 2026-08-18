using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Volatility;

/// <summary>Population standard deviation of Close over the trailing `period` bars.</summary>
public sealed class StandardDeviationIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<decimal> _closes;

    public StandardDeviationIndicator(int period = 20)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _closes = new CircularBuffer<decimal>(period);
    }

    public override string Key => $"StdDev_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => -50; // BollingerBandsIndicator depends on this via context

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _closes.Add(bar.Close);

        if (!_closes.IsFull)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var mean = _closes.Average();
        var sumSquaredDeviations = _closes.Sum(c => (c - mean) * (c - mean));
        var variance = sumSquaredDeviations / _period;
        var stdDev = (decimal)Math.Sqrt((double)variance);

        ctx.Set($"{Key}_Mean", mean);

        return new IndicatorComputation(stdDev, "Neutral", 1m, IndicatorHealth.OK);
    }
}

/// <summary>
/// Bollinger Bands: SMA(period) ± stdDevMultiple * StandardDeviation(period). Reads
/// the mean and stddev from StandardDeviationIndicator's context output rather than
/// recomputing — requires that indicator registered at a lower Priority (it is: -50
/// vs this indicator's default of 0). CurrentValue reports the mid-band (SMA);
/// UpperBand/LowerBand/BandwidthPercent exposed separately.
/// </summary>
public sealed class BollingerBandsIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly decimal _stdDevMultiple;
    private readonly string _stdDevContextKey;
    private readonly string _meanContextKey;

    public BollingerBandsIndicator(int period = 20, decimal stdDevMultiple = 2m)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _stdDevMultiple = stdDevMultiple;
        _stdDevContextKey = $"StdDev_{period}";
        _meanContextKey = $"StdDev_{period}_Mean";
    }

    public decimal? UpperBand { get; private set; }
    public decimal? LowerBand { get; private set; }

    /// <summary>Band width as a percentage of the mid-band — used by the Volatility Engine (§11) for compression/expansion percentile ranking.</summary>
    public decimal? BandwidthPercent { get; private set; }

    public override string Key => $"BollingerBands_{_period}_{_stdDevMultiple}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0; // after StandardDeviationIndicator (-50)

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        if (!ctx.TryGet<decimal?>(_stdDevContextKey, out var stdDevNullable) || stdDevNullable is not { } stdDev
            || !ctx.TryGet<decimal?>(_meanContextKey, out var meanNullable) || meanNullable is not { } mean)
        {
            UpperBand = null;
            LowerBand = null;
            BandwidthPercent = null;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var upper = mean + _stdDevMultiple * stdDev;
        var lower = mean - _stdDevMultiple * stdDev;

        UpperBand = upper;
        LowerBand = lower;
        BandwidthPercent = mean == 0 ? 0m : (upper - lower) / mean * 100m;

        var signal = bar.Close >= upper ? "Overbought" : bar.Close <= lower ? "Oversold" : "Neutral";
        return new IndicatorComputation(mean, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>
/// Historical Volatility: annualized standard deviation of daily log returns over
/// the trailing `period` bars, expressed as a percentage. Distinct from ATR (which
/// measures absolute range) and from Bollinger's raw price-stddev (which isn't
/// annualized) — this is the standard "IV-comparable" realized-vol figure.
/// </summary>
public sealed class HistoricalVolatilityIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly int _annualizationTradingDays;
    private readonly CircularBuffer<decimal> _logReturns;
    private decimal? _previousClose;

    public HistoricalVolatilityIndicator(int period = 20, int annualizationTradingDays = 252)
    {
        if (period <= 1) throw new ArgumentOutOfRangeException(nameof(period), "Historical volatility period must be > 1.");
        _period = period;
        _annualizationTradingDays = annualizationTradingDays;
        _logReturns = new CircularBuffer<decimal>(period);
    }

    public override string Key => $"HistVol_{_period}";
    public override int WarmupPeriod => _period + 1;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        if (_previousClose is null || _previousClose.Value <= 0)
        {
            _previousClose = bar.Close;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var logReturn = (decimal)Math.Log((double)(bar.Close / _previousClose.Value));
        _previousClose = bar.Close;
        _logReturns.Add(logReturn);

        if (!_logReturns.IsFull)
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var mean = _logReturns.Average();
        var sumSquaredDeviations = _logReturns.Sum(r => (r - mean) * (r - mean));
        var dailyStdDev = (decimal)Math.Sqrt((double)(sumSquaredDeviations / (_period - 1))); // sample stddev (n-1)

        var annualizedVolPercent = dailyStdDev * (decimal)Math.Sqrt(_annualizationTradingDays) * 100m;

        // No inherent bullish/bearish direction — SignalState communicates regime instead.
        return new IndicatorComputation(annualizedVolPercent, "Neutral", 1m, IndicatorHealth.OK);
    }
}
