using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Momentum;

/// <summary>
/// MACD: fast EMA - slow EMA, with a signal-line EMA of that difference, and a
/// histogram (MACD - Signal). CurrentValue reports the MACD line itself; Signal and
/// Histogram are exposed as separate properties since IIndicator's single-value
/// contract can't carry all three. Maintains its own internal EMA state rather than
/// depending on separately-registered EmaIndicator instances, since MACD's fast/slow/
/// signal periods are specific to this indicator and shouldn't silently share state
/// with an unrelated general-purpose EMA elsewhere in the pipeline.
/// </summary>
public sealed class MacdIndicator : IndicatorBase
{
    private readonly int _fastPeriod;
    private readonly int _slowPeriod;
    private readonly int _signalPeriod;

    private readonly List<decimal> _seedClosesForFast = new();
    private readonly List<decimal> _seedClosesForSlow = new();
    private readonly List<decimal> _seedMacdForSignal = new();

    private decimal? _previousFastEma;
    private decimal? _previousSlowEma;
    private decimal? _previousSignalEma;

    public MacdIndicator(int fastPeriod = 12, int slowPeriod = 26, int signalPeriod = 9)
    {
        if (fastPeriod <= 0 || slowPeriod <= 0 || signalPeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(fastPeriod), "All MACD periods must be positive.");
        if (fastPeriod >= slowPeriod)
            throw new ArgumentException("MACD fast period must be less than the slow period.");

        _fastPeriod = fastPeriod;
        _slowPeriod = slowPeriod;
        _signalPeriod = signalPeriod;
    }

    public decimal? Signal { get; private set; }
    public decimal? Histogram { get; private set; }

    public override string Key => $"MACD_{_fastPeriod}_{_slowPeriod}_{_signalPeriod}";
    public override int WarmupPeriod => _slowPeriod + _signalPeriod;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var fastEma = ComputeEma(bar.Close, _fastPeriod, _seedClosesForFast, ref _previousFastEma);
        var slowEma = ComputeEma(bar.Close, _slowPeriod, _seedClosesForSlow, ref _previousSlowEma);

        if (fastEma is null || slowEma is null)
        {
            Signal = null;
            Histogram = null;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var macd = fastEma.Value - slowEma.Value;
        var signal = ComputeEma(macd, _signalPeriod, _seedMacdForSignal, ref _previousSignalEma);

        Signal = signal;

        if (signal is null)
        {
            Histogram = null;
            return new IndicatorComputation(macd, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        Histogram = macd - signal.Value;
        var signalState = macd > signal.Value ? "Bullish" : macd < signal.Value ? "Bearish" : "Neutral";

        return new IndicatorComputation(macd, signalState, 1m, IndicatorHealth.OK);
    }

    private static decimal? ComputeEma(decimal value, int period, List<decimal> seedValues, ref decimal? previousEma)
    {
        if (previousEma is null)
        {
            seedValues.Add(value);
            if (seedValues.Count < period)
                return null;

            previousEma = seedValues.Average();
            return previousEma;
        }

        var multiplier = 2m / (period + 1);
        var ema = (value - previousEma.Value) * multiplier + previousEma.Value;
        previousEma = ema;
        return ema;
    }
}
