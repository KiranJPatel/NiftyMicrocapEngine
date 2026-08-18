using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Volume;

/// <summary>Chaikin Money Flow. MFM = ((Close−Low)−(High−Close))/(High−Low); MFV = MFM × Volume; CMF = Σ(MFV, n) / Σ(Volume, n). Range roughly [-1, 1]; positive = accumulation, negative = distribution.</summary>
public sealed class ChaikinMoneyFlowIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<decimal> _moneyFlowVolumes;
    private readonly CircularBuffer<decimal> _volumes;

    public ChaikinMoneyFlowIndicator(int period = 20)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _moneyFlowVolumes = new CircularBuffer<decimal>(period);
        _volumes = new CircularBuffer<decimal>(period);
    }

    public override string Key => $"CMF_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var range = bar.High - bar.Low;
        // A zero-range bar (High == Low, e.g. a circuit-locked session) makes the
        // Money Flow Multiplier undefined (0/0) — treat as no accumulation or
        // distribution that bar rather than propagating a NaN-equivalent.
        var moneyFlowMultiplier = range == 0 ? 0m : ((bar.Close - bar.Low) - (bar.High - bar.Close)) / range;
        var volume = (decimal)bar.Volume;

        _moneyFlowVolumes.Add(moneyFlowMultiplier * volume);
        _volumes.Add(volume);

        if (!_moneyFlowVolumes.IsFull) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var volumeSum = _volumes.Sum();
        var cmf = volumeSum == 0 ? 0m : _moneyFlowVolumes.Sum() / volumeSum;
        var signal = cmf > 0.05m ? "Bullish" : cmf < -0.05m ? "Bearish" : "Neutral";
        return new IndicatorComputation(cmf, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>Money Flow Index — RSI's volume-weighted analogue. TypicalPrice = (H+L+C)/3; RawMoneyFlow = TypicalPrice × Volume, signed by whether TypicalPrice rose or fell versus the prior bar.</summary>
public sealed class MoneyFlowIndexIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<decimal> _positiveFlows, _negativeFlows;
    private decimal? _previousTypicalPrice;

    public MoneyFlowIndexIndicator(int period = 14)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _positiveFlows = new CircularBuffer<decimal>(period);
        _negativeFlows = new CircularBuffer<decimal>(period);
    }

    public override string Key => $"MFI_{_period}";
    public override int WarmupPeriod => _period + 1;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var typicalPrice = (bar.High + bar.Low + bar.Close) / 3m;
        var rawMoneyFlow = typicalPrice * (decimal)bar.Volume;

        if (_previousTypicalPrice is null)
        {
            _previousTypicalPrice = typicalPrice;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var positiveFlow = typicalPrice > _previousTypicalPrice.Value ? rawMoneyFlow : 0m;
        var negativeFlow = typicalPrice < _previousTypicalPrice.Value ? rawMoneyFlow : 0m;
        _previousTypicalPrice = typicalPrice;

        _positiveFlows.Add(positiveFlow);
        _negativeFlows.Add(negativeFlow);

        if (!_positiveFlows.IsFull) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var positiveSum = _positiveFlows.Sum();
        var negativeSum = _negativeFlows.Sum();

        decimal mfi;
        if (negativeSum == 0)
        {
            mfi = positiveSum == 0 ? 50m : 100m;
        }
        else
        {
            var moneyFlowRatio = positiveSum / negativeSum;
            mfi = 100m - 100m / (1m + moneyFlowRatio);
        }

        var signal = mfi >= 80m ? "Overbought" : mfi <= 20m ? "Oversold" : mfi >= 50m ? "Bullish" : "Bearish";
        return new IndicatorComputation(mfi, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>EMA applied to Volume instead of Close — smooths volume trend the same way a price EMA smooths price trend. SignalState compares current volume to its own EMA, not price direction.</summary>
public sealed class VolumeEmaIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly decimal _multiplier;
    private readonly List<decimal> _seedVolumes = new();
    private decimal? _previousEma;

    public VolumeEmaIndicator(int period = 20)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _multiplier = 2m / (period + 1);
    }

    public override string Key => $"VolumeEMA_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var volume = (decimal)bar.Volume;
        decimal ema;

        if (_previousEma is null)
        {
            _seedVolumes.Add(volume);
            if (_seedVolumes.Count < _period) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
            ema = _seedVolumes.Average();
        }
        else
        {
            ema = (volume - _previousEma.Value) * _multiplier + _previousEma.Value;
        }

        _previousEma = ema;
        var signal = volume > ema * 1.5m ? "Bullish" : volume < ema * 0.5m ? "Bearish" : "Neutral"; // "Bullish"/"Bearish" here mean volume surge/drought, not price direction — see class doc comment
        return new IndicatorComputation(ema, signal, 1m, IndicatorHealth.OK);
    }
}
