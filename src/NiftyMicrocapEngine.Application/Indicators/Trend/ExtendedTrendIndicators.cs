using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Trend;

/// <summary>
/// Phase-2 extended set (§7: "pluggable additions, after the core decision
/// engine is validated"). IndicatorValues is a key-value table specifically
/// so these are purely additive — no schema migration, no change to any
/// Phase-1 indicator or the Decision Engine's existing key lookups. None of
/// these are wired into DecisionEngine.LayersPart1/2 yet; that's a
/// deliberate, separate decision (see this file's registration site in
/// StructureAnalysisPipelineFactory) — adding new indicators and changing
/// what the Decision Engine scores on are different risk profiles, and the
/// latter deserves its own review against real chart examples rather than
/// riding in silently alongside a Phase-2 indicator addition.
/// </summary>

/// <summary>Weighted Moving Average — linearly weighted, most recent bar weighted highest. WMA = Σ(price_i × weight_i) / Σ(weight_i), weight_i = position from oldest (1..period).</summary>
public sealed class WmaIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<decimal> _closes;

    public WmaIndicator(int period = 20)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _closes = new CircularBuffer<decimal>(period);
    }

    public override string Key => $"WMA_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _closes.Add(bar.Close);
        if (!_closes.IsFull) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        decimal weightedSum = 0m, weightSum = 0m;
        var position = 1;
        foreach (var close in _closes) // oldest to newest
        {
            weightedSum += close * position;
            weightSum += position;
            position++;
        }

        var wma = weightedSum / weightSum;
        var signal = bar.Close > wma ? "Bullish" : bar.Close < wma ? "Bearish" : "Neutral";
        return new IndicatorComputation(wma, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>Double Exponential Moving Average — reduces EMA's inherent lag. DEMA = 2×EMA1 − EMA(EMA1), EMA1 = EMA(Close, period).</summary>
public sealed class DemaIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly decimal _multiplier;
    private readonly List<decimal> _seedCloses = new();
    private decimal? _ema1;
    private readonly List<decimal> _seedEma1 = new();
    private decimal? _emaOfEma1;

    public DemaIndicator(int period = 20)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _multiplier = 2m / (period + 1);
    }

    public override string Key => $"DEMA_{_period}";
    public override int WarmupPeriod => _period * 2; // needs EMA1 warm, then EMA-of-EMA1 warm
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        // EMA1 = EMA(Close)
        if (_ema1 is null)
        {
            _seedCloses.Add(bar.Close);
            if (_seedCloses.Count < _period) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
            _ema1 = _seedCloses.Average();
        }
        else
        {
            _ema1 = (bar.Close - _ema1.Value) * _multiplier + _ema1.Value;
        }

        // EMA-of-EMA1
        if (_emaOfEma1 is null)
        {
            _seedEma1.Add(_ema1.Value);
            if (_seedEma1.Count < _period) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
            _emaOfEma1 = _seedEma1.Average();
        }
        else
        {
            _emaOfEma1 = (_ema1.Value - _emaOfEma1.Value) * _multiplier + _emaOfEma1.Value;
        }

        var dema = 2m * _ema1.Value - _emaOfEma1.Value;
        var signal = bar.Close > dema ? "Bullish" : bar.Close < dema ? "Bearish" : "Neutral";
        return new IndicatorComputation(dema, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>Triple Exponential Moving Average — TRIX's underlying MA. TEMA = 3×EMA1 − 3×EMA2 + EMA3, EMA2 = EMA(EMA1), EMA3 = EMA(EMA2).</summary>
public sealed class TemaIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly decimal _multiplier;
    private readonly List<decimal> _seed1 = new(), _seed2 = new(), _seed3 = new();
    private decimal? _ema1, _ema2, _ema3;

    public TemaIndicator(int period = 20)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _multiplier = 2m / (period + 1);
    }

    public override string Key => $"TEMA_{_period}";
    public override int WarmupPeriod => _period * 3;
    public override int Priority => 0;

    private static decimal? StepEma(decimal input, ref decimal? previous, List<decimal> seed, int period)
    {
        if (previous is null)
        {
            seed.Add(input);
            if (seed.Count < period) return null;
            previous = seed.Average();
        }
        else
        {
            previous = (input - previous.Value) * (2m / (period + 1)) + previous.Value;
        }
        return previous;
    }

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        var e1 = StepEma(bar.Close, ref _ema1, _seed1, _period);
        if (e1 is null) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var e2 = StepEma(e1.Value, ref _ema2, _seed2, _period);
        if (e2 is null) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var e3 = StepEma(e2.Value, ref _ema3, _seed3, _period);
        if (e3 is null) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var tema = 3m * e1.Value - 3m * e2.Value + e3.Value;
        var signal = bar.Close > tema ? "Bullish" : bar.Close < tema ? "Bearish" : "Neutral";
        return new IndicatorComputation(tema, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>
/// Kaufman's Adaptive Moving Average — adapts its own smoothing speed to
/// trending vs. choppy conditions via an Efficiency Ratio. Standard fast/slow
/// constants: fast period 2 (fastSC ≈ 0.667), slow period 30 (slowSC ≈ 0.0645).
/// </summary>
public sealed class KamaIndicator : IndicatorBase
{
    private readonly int _erPeriod;
    private readonly decimal _fastSc;
    private readonly decimal _slowSc;
    private readonly CircularBuffer<decimal> _closes;
    private decimal? _kama;

    public KamaIndicator(int erPeriod = 10, int fastPeriod = 2, int slowPeriod = 30)
    {
        if (erPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(erPeriod));
        _erPeriod = erPeriod;
        _fastSc = 2m / (fastPeriod + 1);
        _slowSc = 2m / (slowPeriod + 1);
        _closes = new CircularBuffer<decimal>(erPeriod + 1);
    }

    public override string Key => $"KAMA_{_erPeriod}";
    public override int WarmupPeriod => _erPeriod + 1;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _closes.Add(bar.Close);
        if (!_closes.IsFull) return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);

        var change = Math.Abs(_closes[0] - _closes[_closes.Count - 1]); // newest - oldest
        decimal volatility = 0m;
        for (var i = 0; i < _closes.Count - 1; i++)
        {
            volatility += Math.Abs(_closes[i] - _closes[i + 1]);
        }

        var efficiencyRatio = volatility == 0m ? 0m : change / volatility;
        var smoothingConstant = efficiencyRatio * (_fastSc - _slowSc) + _slowSc;
        var smoothingSquared = smoothingConstant * smoothingConstant;

        _kama = _kama is null ? bar.Close : _kama.Value + smoothingSquared * (bar.Close - _kama.Value);

        var signal = bar.Close > _kama ? "Bullish" : bar.Close < _kama ? "Bearish" : "Neutral";
        return new IndicatorComputation(_kama, signal, 1m, IndicatorHealth.OK);
    }
}

/// <summary>
/// Linear regression channel: fits a least-squares line to Close over
/// `period`, exposes the line's current-bar value as CurrentValue, and
/// UpperBand/LowerBand at ± `stdDevMultiplier` residual standard deviations
/// — a statistically-fit alternative to Bollinger's SMA-centered bands.
/// </summary>
public sealed class RegressionChannelIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly decimal _stdDevMultiplier;
    private readonly CircularBuffer<decimal> _closes;

    public RegressionChannelIndicator(int period = 20, decimal stdDevMultiplier = 2m)
    {
        if (period <= 1) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _stdDevMultiplier = stdDevMultiplier;
        _closes = new CircularBuffer<decimal>(period);
    }

    public decimal? UpperBand { get; private set; }
    public decimal? LowerBand { get; private set; }
    public decimal? SlopePerBar { get; private set; }

    public override string Key => $"RegressionChannel_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _closes.Add(bar.Close);
        if (!_closes.IsFull)
        {
            UpperBand = null; LowerBand = null; SlopePerBar = null;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        // x = 0..n-1 oldest to newest; standard least-squares slope/intercept.
        var n = _closes.Count;
        decimal sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        var x = 0;
        foreach (var y in _closes)
        {
            sumX += x; sumY += y; sumXY += x * y; sumXX += (decimal)x * x;
            x++;
        }

        var denominator = n * sumXX - sumX * sumX;
        var slope = denominator == 0 ? 0m : (n * sumXY - sumX * sumY) / denominator;
        var intercept = (sumY - slope * sumX) / n;

        // Regression line's value at the most recent bar (x = n-1).
        var fittedAtLatest = intercept + slope * (n - 1);

        decimal sumSquaredResiduals = 0;
        x = 0;
        foreach (var y in _closes)
        {
            var fitted = intercept + slope * x;
            var residual = y - fitted;
            sumSquaredResiduals += residual * residual;
            x++;
        }
        var residualStdDev = (decimal)Math.Sqrt((double)(sumSquaredResiduals / n));

        SlopePerBar = slope;
        UpperBand = fittedAtLatest + _stdDevMultiplier * residualStdDev;
        LowerBand = fittedAtLatest - _stdDevMultiplier * residualStdDev;

        var signal = slope > 0 ? "Bullish" : slope < 0 ? "Bearish" : "Neutral";
        return new IndicatorComputation(fittedAtLatest, signal, 1m, IndicatorHealth.OK);
    }
}
