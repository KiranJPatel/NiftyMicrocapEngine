using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Momentum;

/// <summary>
/// Stochastic RSI: the Stochastic %K/%D formula applied to RSI values
/// instead of price. Computes RSI internally (Wilder-smoothed, same
/// convention as RsiIndicator) rather than reading another instance's
/// "RSI_14" key off IProcessingContext — keeps this indicator self-contained
/// and independently configurable (a different RSI period here doesn't
/// silently depend on some other RsiIndicator instance happening to be
/// registered with a matching period in the same pipeline).
/// </summary>
public sealed class StochasticRsiIndicator : IndicatorBase
{
    private readonly int _rsiPeriod, _stochPeriod, _dPeriod;
    private readonly List<decimal> _seedGains = new(), _seedLosses = new();
    private decimal? _previousClose, _avgGain, _avgLoss;
    private readonly CircularBuffer<decimal> _rsiValues;
    private readonly CircularBuffer<decimal> _percentKHistory;

    public StochasticRsiIndicator(int rsiPeriod = 14, int stochPeriod = 14, int dPeriod = 3)
    {
        if (rsiPeriod <= 0 || stochPeriod <= 0 || dPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(rsiPeriod));
        _rsiPeriod = rsiPeriod;
        _stochPeriod = stochPeriod;
        _dPeriod = dPeriod;
        _rsiValues = new CircularBuffer<decimal>(stochPeriod);
        _percentKHistory = new CircularBuffer<decimal>(dPeriod);
    }

    public decimal? PercentD { get; private set; }

    public override string Key => $"StochRSI_{_rsiPeriod}_{_stochPeriod}_{_dPeriod}";
    public override int WarmupPeriod => _rsiPeriod + _stochPeriod + _dPeriod;
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
            if (_seedGains.Count < _rsiPeriod) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
            avgGain = _seedGains.Average();
            avgLoss = _seedLosses.Average();
        }
        else
        {
            avgGain = (_avgGain.Value * (_rsiPeriod - 1) + gain) / _rsiPeriod;
            avgLoss = (_avgLoss!.Value * (_rsiPeriod - 1) + loss) / _rsiPeriod;
        }
        _avgGain = avgGain;
        _avgLoss = avgLoss;

        var rsi = avgLoss == 0 ? (avgGain == 0 ? 50m : 100m) : 100m - 100m / (1m + avgGain / avgLoss);
        _rsiValues.Add(rsi);

        if (!_rsiValues.IsFull)
        {
            PercentD = null;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var highestRsi = _rsiValues.Max();
        var lowestRsi = _rsiValues.Min();
        var range = highestRsi - lowestRsi;
        var stochRsi = range == 0 ? 50m : (rsi - lowestRsi) / range * 100m;

        _percentKHistory.Add(stochRsi);
        if (!_percentKHistory.IsFull)
        {
            PercentD = null;
            return new IndicatorComputation(stochRsi, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }
        PercentD = _percentKHistory.Average();

        var signal = stochRsi >= 80m ? "Overbought" : stochRsi <= 20m ? "Oversold" : stochRsi >= 50m ? "Bullish" : "Bearish";
        return new IndicatorComputation(stochRsi, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>Commodity Channel Index. CCI = (TypicalPrice − SMA(TypicalPrice, n)) / (0.015 × MeanAbsoluteDeviation).</summary>
public sealed class CciIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<decimal> _typicalPrices;

    public CciIndicator(int period = 20)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _typicalPrices = new CircularBuffer<decimal>(period);
    }

    public override string Key => $"CCI_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var typicalPrice = (bar.High + bar.Low + bar.Close) / 3m;
        _typicalPrices.Add(typicalPrice);

        if (!_typicalPrices.IsFull) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var sma = _typicalPrices.Average();
        var meanAbsoluteDeviation = _typicalPrices.Select(tp => Math.Abs(tp - sma)).Average();

        var cci = meanAbsoluteDeviation == 0 ? 0m : (typicalPrice - sma) / (0.015m * meanAbsoluteDeviation);
        var signal = cci >= 100m ? "Overbought" : cci <= -100m ? "Oversold" : cci >= 0 ? "Bullish" : "Bearish";
        return new IndicatorComputation(cci, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>Rate of Change. ROC = (Close − Close[n bars ago]) / Close[n bars ago] × 100.</summary>
public sealed class RocIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<decimal> _closes;

    public RocIndicator(int period = 12)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _closes = new CircularBuffer<decimal>(period + 1);
    }

    public override string Key => $"ROC_{_period}";
    public override int WarmupPeriod => _period + 1;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _closes.Add(bar.Close);
        if (!_closes.IsFull) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var closeNBarsAgo = _closes[_closes.Count - 1]; // oldest in the (period+1)-sized window = exactly `period` bars before newest
        var roc = closeNBarsAgo == 0 ? 0m : (bar.Close - closeNBarsAgo) / closeNBarsAgo * 100m;
        var signal = roc > 0 ? "Bullish" : roc < 0 ? "Bearish" : "Neutral";
        return new IndicatorComputation(roc, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>Williams %R. %R = (HighestHigh − Close) / (HighestHigh − LowestLow) × −100. Range [-100, 0]; near 0 = overbought, near -100 = oversold (inverse polarity from Stochastic %K, by convention).</summary>
public sealed class WilliamsRIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<decimal> _highs, _lows;

    public WilliamsRIndicator(int period = 14)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _highs = new CircularBuffer<decimal>(period);
        _lows = new CircularBuffer<decimal>(period);
    }

    public override string Key => $"WilliamsR_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _highs.Add(bar.High);
        _lows.Add(bar.Low);
        if (!_highs.IsFull) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var highestHigh = _highs.Max();
        var lowestLow = _lows.Min();
        var range = highestHigh - lowestLow;

        var williamsR = range == 0 ? -50m : (highestHigh - bar.Close) / range * -100m;
        var signal = williamsR >= -20m ? "Overbought" : williamsR <= -80m ? "Oversold" : williamsR >= -50m ? "Bullish" : "Bearish";
        return new IndicatorComputation(williamsR, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>TRIX: percentage rate of change of a triple-smoothed EMA. Filters minor short-term fluctuations more aggressively than MACD.</summary>
public sealed class TrixIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly List<decimal> _seed1 = new(), _seed2 = new(), _seed3 = new();
    private decimal? _ema1, _ema2, _ema3, _previousEma3;

    public TrixIndicator(int period = 15)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
    }

    public override string Key => $"TRIX_{_period}";
    public override int WarmupPeriod => _period * 3 + 1;
    public override int Priority => 0;

    private decimal? StepEma(decimal input, ref decimal? previous, List<decimal> seed)
    {
        if (previous is null)
        {
            seed.Add(input);
            if (seed.Count < _period) return null;
            previous = seed.Average();
        }
        else
        {
            previous = (input - previous.Value) * (2m / (_period + 1)) + previous.Value;
        }
        return previous;
    }

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var e1 = StepEma(bar.Close, ref _ema1, _seed1);
        if (e1 is null) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var e2 = StepEma(e1.Value, ref _ema2, _seed2);
        if (e2 is null) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var e3 = StepEma(e2.Value, ref _ema3, _seed3);
        if (e3 is null) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        if (_previousEma3 is null)
        {
            _previousEma3 = e3.Value;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var trix = _previousEma3.Value == 0 ? 0m : (e3.Value - _previousEma3.Value) / _previousEma3.Value * 100m;
        _previousEma3 = e3.Value;

        var signal = trix > 0 ? "Bullish" : trix < 0 ? "Bearish" : "Neutral";
        return new IndicatorComputation(trix, signal, 1m, IndicatorHealth.OK);
    }
}
