using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Momentum;

/// <summary>
/// Relative Strength Index, Wilder-smoothed (the standard RSI definition — same
/// smoothing convention as ATR). RSI = 100 - 100/(1 + AvgGain/AvgLoss).
/// </summary>
public sealed class RsiIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly List<decimal> _seedGains = new();
    private readonly List<decimal> _seedLosses = new();
    private decimal? _previousClose;
    private decimal? _avgGain;
    private decimal? _avgLoss;

    public RsiIndicator(int period = 14)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
    }

    public override string Key => $"RSI_{_period}";
    public override int WarmupPeriod => _period + 1;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        if (_previousClose is null)
        {
            _previousClose = bar.Close;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var change = bar.Close - _previousClose.Value;
        var gain = Math.Max(0m, change);
        var loss = Math.Max(0m, -change);
        _previousClose = bar.Close;

        decimal avgGain, avgLoss;

        if (_avgGain is null)
        {
            _seedGains.Add(gain);
            _seedLosses.Add(loss);

            if (_seedGains.Count < _period)
                return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

            avgGain = _seedGains.Average();
            avgLoss = _seedLosses.Average();
        }
        else
        {
            avgGain = (_avgGain.Value * (_period - 1) + gain) / _period;
            avgLoss = (_avgLoss!.Value * (_period - 1) + loss) / _period;
        }

        _avgGain = avgGain;
        _avgLoss = avgLoss;

        decimal rsi;
        if (avgLoss == 0)
        {
            rsi = avgGain == 0 ? 50m : 100m;
        }
        else
        {
            var rs = avgGain / avgLoss;
            rsi = 100m - 100m / (1m + rs);
        }

        var signal = rsi >= 70m ? "Overbought" : rsi <= 30m ? "Oversold" : rsi >= 50m ? "Bullish" : "Bearish";
        return new IndicatorComputation(rsi, signal, 1m, IndicatorHealth.OK);
    }
}
