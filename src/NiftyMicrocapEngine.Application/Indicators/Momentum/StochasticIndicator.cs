using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Momentum;

/// <summary>
/// Stochastic Oscillator: %K = 100 * (Close - LowestLow) / (HighestHigh - LowestLow)
/// over `kPeriod`, %D = SMA(%K, dPeriod). CurrentValue reports %K; %D is exposed
/// separately (same single-value-contract reasoning as MACD's Signal/Histogram).
/// </summary>
public sealed class StochasticIndicator : IndicatorBase
{
    private readonly int _kPeriod;
    private readonly int _dPeriod;
    private readonly CircularBuffer<decimal> _highs;
    private readonly CircularBuffer<decimal> _lows;
    private readonly CircularBuffer<decimal> _percentKHistory;

    public StochasticIndicator(int kPeriod = 14, int dPeriod = 3)
    {
        if (kPeriod <= 0 || dPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(kPeriod));
        _kPeriod = kPeriod;
        _dPeriod = dPeriod;
        _highs = new CircularBuffer<decimal>(kPeriod);
        _lows = new CircularBuffer<decimal>(kPeriod);
        _percentKHistory = new CircularBuffer<decimal>(dPeriod);
    }

    public decimal? PercentD { get; private set; }

    public override string Key => $"Stochastic_{_kPeriod}_{_dPeriod}";
    public override int WarmupPeriod => _kPeriod + _dPeriod;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _highs.Add(bar.High);
        _lows.Add(bar.Low);

        if (!_highs.IsFull)
        {
            PercentD = null;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var highestHigh = _highs.Max();
        var lowestLow = _lows.Min();
        var range = highestHigh - lowestLow;

        var percentK = range == 0 ? 50m : (bar.Close - lowestLow) / range * 100m;
        _percentKHistory.Add(percentK);

        if (!_percentKHistory.IsFull)
        {
            PercentD = null;
            return new IndicatorComputation(percentK, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        PercentD = _percentKHistory.Average();

        var signal = percentK >= 80m ? "Overbought" : percentK <= 20m ? "Oversold" : percentK >= 50m ? "Bullish" : "Bearish";
        return new IndicatorComputation(percentK, signal, 1m, IndicatorHealth.OK);
    }
}
