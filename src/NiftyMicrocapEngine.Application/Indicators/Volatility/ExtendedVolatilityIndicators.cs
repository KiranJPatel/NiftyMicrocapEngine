using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Volatility;

/// <summary>
/// Keltner Channel: Middle = EMA(Close, period); Upper/Lower = Middle ±
/// atrMultiplier × ATR(period). Computes its own internal ATR rather than
/// reading another instance's "ATR_14" key off IProcessingContext, for the
/// same self-containment reason as StochasticRsiIndicator's internal RSI —
/// a Keltner Channel configured with a non-standard period shouldn't
/// silently depend on some other AtrIndicator happening to share that
/// period in the same pipeline.
/// </summary>
public sealed class KeltnerChannelIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly decimal _atrMultiplier;
    private readonly List<decimal> _seedCloses = new();
    private decimal? _previousEma;
    private readonly List<decimal> _seedTrueRanges = new();
    private decimal? _previousAtr;
    private decimal? _previousClose;

    public KeltnerChannelIndicator(int period = 20, decimal atrMultiplier = 2m)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _atrMultiplier = atrMultiplier;
    }

    public decimal? UpperBand { get; private set; }
    public decimal? LowerBand { get; private set; }

    public override string Key => $"KeltnerChannel_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        // BUG FIXED HERE (caught while hand-tracing expected test values,
        // not by running anything): the EMA and ATR seed accumulations were
        // structured so ATR's seeding code only ran once EMA's warmup
        // finished — i.e., EMA's "not enough bars yet" branch returned
        // BEFORE the ATR block below it ever executed, so _seedTrueRanges
        // stayed empty for the first `period-1` bars. Real warmup ended up
        // needing close to 2×period bars, silently contradicting
        // WarmupPeriod's declared value of `period`. Both seeds now
        // accumulate independently, every bar, from bar 1 — true warmup now
        // matches WarmupPeriod exactly.

        // Middle line: EMA(Close, period)
        decimal? ema;
        if (_previousEma is null)
        {
            _seedCloses.Add(bar.Close);
            ema = _seedCloses.Count >= _period ? _seedCloses.Average() : (decimal?)null;
        }
        else
        {
            ema = (bar.Close - _previousEma.Value) * (2m / (_period + 1)) + _previousEma.Value;
        }
        if (ema is not null) _previousEma = ema;

        // Internal ATR(period), Wilder-smoothed, same formula as AtrIndicator.
        // trueRange/previousClose update unconditionally, every bar,
        // independent of whether EMA has finished warming up yet.
        var range = bar.High - bar.Low;
        var trueRange = _previousClose is null ? range : Math.Max(range, Math.Max(Math.Abs(bar.High - _previousClose.Value), Math.Abs(bar.Low - _previousClose.Value)));
        _previousClose = bar.Close;

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
        if (atr is not null) _previousAtr = atr;

        if (ema is null || atr is null)
        {
            UpperBand = null; LowerBand = null;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        UpperBand = ema.Value + _atrMultiplier * atr.Value;
        LowerBand = ema.Value - _atrMultiplier * atr.Value;

        var signal = bar.Close > UpperBand ? "Bullish" : bar.Close < LowerBand ? "Bearish" : "Neutral"; // price outside the channel = breakout, not just trend direction
        return new IndicatorComputation(ema.Value, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>
/// Range Compression/Expansion Detection: not a classic single-line
/// indicator — flags volatility-squeeze conditions a swing trader would
/// watch for a breakout out of. CurrentValue = ratio of today's True Range
/// to the rolling average True Range over `period` (e.g. 0.6 = today's range
/// is 60% of the recent average — a squeeze; 1.8 = an expansion day).
/// SignalState surfaces the state directly rather than forcing the caller to
/// interpret the ratio.
/// </summary>
public sealed class RangeCompressionExpansionIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly decimal _compressionThreshold;
    private readonly decimal _expansionThreshold;
    private readonly CircularBuffer<decimal> _trueRanges;
    private decimal? _previousClose;

    public RangeCompressionExpansionIndicator(int period = 20, decimal compressionThreshold = 0.6m, decimal expansionThreshold = 1.5m)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _compressionThreshold = compressionThreshold;
        _expansionThreshold = expansionThreshold;
        _trueRanges = new CircularBuffer<decimal>(period);
    }

    public override string Key => $"RangeCompressionExpansion_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var range = bar.High - bar.Low;
        var trueRange = _previousClose is null ? range : Math.Max(range, Math.Max(Math.Abs(bar.High - _previousClose.Value), Math.Abs(bar.Low - _previousClose.Value)));
        _previousClose = bar.Close;

        _trueRanges.Add(trueRange);
        if (!_trueRanges.IsFull) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var averageTrueRange = _trueRanges.Average();
        var ratio = averageTrueRange == 0 ? 1m : trueRange / averageTrueRange;

        var signal = ratio <= _compressionThreshold ? "Compression" : ratio >= _expansionThreshold ? "Expansion" : "Neutral";
        return new IndicatorComputation(ratio, signal, 1m, IndicatorHealth.OK);
    }
}
