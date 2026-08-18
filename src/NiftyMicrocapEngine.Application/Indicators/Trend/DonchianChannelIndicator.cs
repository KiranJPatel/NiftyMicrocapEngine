using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Trend;

/// <summary>
/// Donchian Channel: highest High and lowest Low over the trailing `period` bars.
/// CurrentValue reports the midline; upper/lower bands are exposed separately since
/// IIndicator's contract (§7) only carries a single CurrentValue — callers needing
/// the bands read UpperBand/LowerBand directly off this concrete type.
/// </summary>
public sealed class DonchianChannelIndicator : IndicatorBase
{
    private readonly int _period;
    private readonly CircularBuffer<decimal> _highs;
    private readonly CircularBuffer<decimal> _lows;

    public DonchianChannelIndicator(int period = 20)
    {
        if (period <= 0) throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _highs = new CircularBuffer<decimal>(period);
        _lows = new CircularBuffer<decimal>(period);
    }

    public decimal? UpperBand { get; private set; }
    public decimal? LowerBand { get; private set; }

    public override string Key => $"Donchian_{_period}";
    public override int WarmupPeriod => _period;
    public override int Priority => 0;

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _highs.Add(bar.High);
        _lows.Add(bar.Low);

        if (!_highs.IsFull)
        {
            UpperBand = null;
            LowerBand = null;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var upper = _highs.Max();
        var lower = _lows.Min();
        var mid = (upper + lower) / 2m;

        UpperBand = upper;
        LowerBand = lower;

        var signal = bar.Close >= upper ? "Bullish" : bar.Close <= lower ? "Bearish" : "Neutral";
        ctx.Set($"{Key}_Upper", upper);
        ctx.Set($"{Key}_Lower", lower);

        return new IndicatorComputation(mid, signal, 1m, IndicatorHealth.OK);
    }
}
