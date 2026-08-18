using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Structure;

/// <summary>
/// Tracks price legs between confirmed swing points and classifies each as Impulse,
/// Correction, Trend, or Range per §8. Depends on ATR (via context, written by an
/// AtrIndicator at Priority -100) and on swing/break output (SwingPointDetector at
/// -200, StructureBreakDetector at -190) — so this must run at a higher Priority
/// than all three in the same pipeline pass.
///
/// Rule recap (§8):
///   Impulse leg    = range ge 1.5xATR(14) OR produces a BOS within 3 candles of the leg's start
///   Correction     = counter-trend leg following an impulse leg that does NOT itself produce a CHoCH
///   Trend leg      = any leg between two confirmed swings in the direction of the prevailing structure
///   Range          = price contained between the most recent unbroken swing high/low for ge N candles (default 10) with no BOS
/// </summary>
public sealed class ImpulseLegClassifier : IBarProcessor
{
    private readonly int _symbolId;
    private readonly Timeframe _timeframe;
    private readonly StructureThresholds _thresholds;
    private readonly string _atrContextKey;

    private readonly List<PriceLeg> _legs = new();
    private Candle? _legStartCandle;
    private int _candlesSinceLegStart;
    private int _candlesSinceLastBos;
    private bool _lastLegWasImpulse;
    private TrendDirection _lastImpulseDirection = TrendDirection.Ranging;

    // Range tracking: counts consecutive candles contained within the current
    // unbroken swing-high/swing-low band with no intervening BOS.
    private int _consecutiveRangeCandles;
    private decimal? _rangeUpperBound;
    private decimal? _rangeLowerBound;

    public ImpulseLegClassifier(int symbolId, Timeframe timeframe, StructureThresholds thresholds)
    {
        _symbolId = symbolId;
        _timeframe = timeframe;
        _thresholds = thresholds;
        _atrContextKey = $"ATR_{thresholds.AtrPeriod}";
    }

    public int Priority => -180; // after StructureBreakDetector (-190)

    public IReadOnlyList<PriceLeg> Legs => _legs;
    public bool IsCurrentlyRanging => _consecutiveRangeCandles >= _thresholds.RangeMinCandles;

    public Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct)
    {
        var newSwing = ctx.TryGet<SwingPoint?>("Structure.NewSwing", out var s) ? s : null;
        var newBreak = ctx.TryGet<StructureBreakEvent?>("Structure.NewBreak", out var b) ? b : null;
        var prevailingTrend = ctx.TryGet<TrendDirection>("Structure.PrevailingTrend", out var t) ? t : TrendDirection.Ranging;
        var atr = ctx.TryGet<decimal?>(_atrContextKey, out var a) ? a : null;

        _candlesSinceLastBos = newBreak is { Kind: StructureBreakKind.BOS } ? 0 : _candlesSinceLastBos + 1;

        // --- Range tracking: reset on any BOS; extend while price stays within the
        // most recent unbroken swing band. ---
        if (newBreak is not null)
        {
            _consecutiveRangeCandles = 0;
            _rangeUpperBound = null;
            _rangeLowerBound = null;
        }
        else if (ctx.TryGet<IReadOnlyList<SwingPoint>>("Structure.AllSwings", out var allSwings) && allSwings is { Count: > 0 })
        {
            var unbrokenHigh = allSwings.LastOrDefault(sw => sw.Type == SwingType.High && !sw.IsBroken);
            var unbrokenLow = allSwings.LastOrDefault(sw => sw.Type == SwingType.Low && !sw.IsBroken);

            if (unbrokenHigh is not null && unbrokenLow is not null)
            {
                _rangeUpperBound = unbrokenHigh.Price;
                _rangeLowerBound = unbrokenLow.Price;

                var containedWithinBand = bar.High <= _rangeUpperBound && bar.Low >= _rangeLowerBound;
                _consecutiveRangeCandles = containedWithinBand ? _consecutiveRangeCandles + 1 : 0;
            }
        }

        // --- Leg tracking: start a new leg at each confirmed swing point. ---
        PriceLeg? completedLeg = null;

        if (newSwing is not null)
        {
            if (_legStartCandle is not null)
            {
                completedLeg = ClassifyAndRecordLeg(_legStartCandle, bar, prevailingTrend, atr);
            }

            _legStartCandle = bar;
            _candlesSinceLegStart = 0;
        }
        else
        {
            _candlesSinceLegStart++;
        }

        ctx.Set("Structure.CompletedLeg", completedLeg);
        ctx.Set("Structure.IsRanging", IsCurrentlyRanging);

        return Task.CompletedTask;
    }

    private PriceLeg ClassifyAndRecordLeg(Candle startCandle, Candle endCandle, TrendDirection prevailingTrend, decimal? atr)
    {
        var range = Math.Abs(endCandle.Close - startCandle.Close);
        var direction = endCandle.Close >= startCandle.Close ? TrendDirection.Bullish : TrendDirection.Bearish;

        var meetsAtrThreshold = atr is > 0 && range >= _thresholds.ImpulseAtrMultiple * atr.Value;
        var producedBosQuickly = _candlesSinceLastBos <= _thresholds.ImpulseBosLookaheadCandles;
        var isImpulse = meetsAtrThreshold || producedBosQuickly;

        LegKind kind;
        if (isImpulse)
        {
            kind = LegKind.Impulse;
        }
        else if (_lastLegWasImpulse && direction != _lastImpulseDirection)
        {
            // Counter-trend leg following an impulse, and (by falling through the
            // isImpulse check above) it did NOT itself produce a CHoCH -> Correction.
            kind = LegKind.Correction;
        }
        else if (direction == prevailingTrend)
        {
            kind = LegKind.Trend;
        }
        else
        {
            // Falls through §8's four categories only when none cleanly apply
            // (e.g. no established prevailing trend yet) — default to Trend as the
            // least specific "leg between two swings" classification rather than
            // inventing a fifth category the spec doesn't define.
            kind = LegKind.Trend;
        }

        if (isImpulse)
        {
            _lastLegWasImpulse = true;
            _lastImpulseDirection = direction;
        }
        else if (kind != LegKind.Correction)
        {
            _lastLegWasImpulse = false;
        }

        var leg = new PriceLeg(_symbolId, _timeframe, startCandle.Timestamp, endCandle.Timestamp, startCandle.Close, endCandle.Close, kind, direction);
        _legs.Add(leg);
        return leg;
    }
}
