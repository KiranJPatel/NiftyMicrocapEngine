using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Structure;

/// <summary>
/// Detects the remaining section-9 SMC events not covered by SmcZoneDetector:
/// liquidity grabs, bull/bear traps, false breakouts/failed breakdowns, volume
/// absorption, exhaustion candles, and gap classification (breakaway/continuation/
/// exhaustion). Runs after SwingPointDetector, StructureBreakDetector, and
/// ImpulseLegClassifier (all of which it reads from context), and after ATR/
/// VolumeSMA indicators.
///
/// Rule recap:
/// Liquidity grab: a wick pierces beyond a marked, unmitigated prior swing point,
/// but the candle's Close remains back inside the prior range.
/// Bull/Bear trap: a confirmed BOS that reverses, closing back inside the broken
/// range within K candles (default 3).
/// False breakout / Failed breakdown: price exceeds a Range's boundary intra-candle
/// but closes back inside the Range on the same or next candle.
/// Volume absorption: Volume at or above 2x the 20-period Volume SMA but Body%
/// under 30% of Range, occurring at a marked supply/demand zone or swing point.
/// Exhaustion candle: Range at or above 2x ATR(14) with Close in the outer 20% of
/// the range against the prevailing trend direction.
/// Gap breakaway: a price gap at the start of an impulse leg that breaks out of a
/// prior Range.
/// Gap continuation: a price gap mid-trend in the direction of the prevailing
/// trend, not at a Range boundary.
/// Gap exhaustion: a price gap late in an established trend (after 3+ same-
/// direction trend legs) followed by a reversal (CHoCH) within K candles (default 5).
/// </summary>
public sealed class SmcEventDetector : IBarProcessor
{
    private readonly int _symbolId;
    private readonly Timeframe _timeframe;
    private readonly StructureThresholds _thresholds;
    private readonly string _atrContextKey;
    private readonly string _volumeSmaContextKey;

    private readonly List<SmcEvent> _events = new();
    private Candle? _previousCandle;

    // Pending BOS events awaiting a trap-reversal check within K candles.
    private readonly List<(StructureBreakEvent Break, int CandlesSince)> _pendingBosForTrapCheck = new();

    // Trend-leg run tracking for gap-exhaustion.
    private int _sameDirectionTrendLegRun;
    private TrendDirection _lastTrendLegDirection = TrendDirection.Ranging;
    private readonly List<(DateTimeOffset Timestamp, int CandlesSince)> _pendingGapExhaustionChecks = new();

    public SmcEventDetector(int symbolId, Timeframe timeframe, StructureThresholds thresholds)
    {
        _symbolId = symbolId;
        _timeframe = timeframe;
        _thresholds = thresholds;
        _atrContextKey = $"ATR_{thresholds.AtrPeriod}";
        _volumeSmaContextKey = $"VolumeSMA_{thresholds.VolumeSmaPeriodForAbsorption}";
    }

    public int Priority => -160; // after SmcZoneDetector (-170)

    public IReadOnlyList<SmcEvent> Events => _events;

    public Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct)
    {
        var atr = ctx.TryGet<decimal?>(_atrContextKey, out var a) ? a : null;
        var volumeSma = ctx.TryGet<decimal?>(_volumeSmaContextKey, out var v) ? v : null;
        var prevailingTrend = ctx.TryGet<TrendDirection>("Structure.PrevailingTrend", out var t) ? t : TrendDirection.Ranging;
        var newBreak = ctx.TryGet<StructureBreakEvent?>("Structure.NewBreak", out var nb) ? nb : null;
        var completedLeg = ctx.TryGet<PriceLeg?>("Structure.CompletedLeg", out var cl) ? cl : null;
        var isRanging = ctx.TryGet<bool>("Structure.IsRanging", out var ir) && ir;

        DetectLiquidityGrab(bar, ctx);
        DetectTrapReversal(bar, newBreak);
        DetectFalseBreakout(bar, isRanging, ctx);
        DetectVolumeAbsorption(bar, ctx, volumeSma);
        DetectExhaustionCandle(bar, atr, prevailingTrend);
        DetectGaps(bar, completedLeg, isRanging, prevailingTrend);
        CheckPendingGapExhaustion(bar.Timestamp, newBreak);
        TrackTrendLegRun(completedLeg);

        _previousCandle = bar;
        return Task.CompletedTask;
    }

    private void DetectLiquidityGrab(Candle bar, IProcessingContext ctx)
    {
        if (!ctx.TryGet<IReadOnlyList<SwingPoint>>("Structure.AllSwings", out var swings) || swings is not { Count: > 0 }) return;

        var unbrokenHigh = swings.LastOrDefault(s => s.Type == SwingType.High && !s.IsBroken);
        var unbrokenLow = swings.LastOrDefault(s => s.Type == SwingType.Low && !s.IsBroken);

        if (unbrokenHigh is not null && bar.High > unbrokenHigh.Price && bar.Close <= unbrokenHigh.Price)
        {
            _events.Add(new SmcEvent(_symbolId, _timeframe, bar.Timestamp, SmcEventKind.LiquidityGrab, $"Wick above swing high {unbrokenHigh.Price}, closed back inside."));
        }

        if (unbrokenLow is not null && bar.Low < unbrokenLow.Price && bar.Close >= unbrokenLow.Price)
        {
            _events.Add(new SmcEvent(_symbolId, _timeframe, bar.Timestamp, SmcEventKind.LiquidityGrab, $"Wick below swing low {unbrokenLow.Price}, closed back inside."));
        }
    }

    private void DetectTrapReversal(Candle bar, StructureBreakEvent? newBreak)
    {
        if (newBreak is { Kind: StructureBreakKind.BOS })
        {
            _pendingBosForTrapCheck.Add((newBreak, 0));
        }

        for (var i = _pendingBosForTrapCheck.Count - 1; i >= 0; i--)
        {
            var (breakEvent, candlesSince) = _pendingBosForTrapCheck[i];
            var updatedCandlesSince = candlesSince + 1;

            var reversedBack = breakEvent.NewDirection == TrendDirection.Bullish
                ? bar.Close < breakEvent.BrokenSwing.Price
                : bar.Close > breakEvent.BrokenSwing.Price;

            if (reversedBack)
            {
                var kind = breakEvent.NewDirection == TrendDirection.Bullish ? SmcEventKind.BullTrap : SmcEventKind.BearTrap;
                _events.Add(new SmcEvent(_symbolId, _timeframe, bar.Timestamp, kind, $"BOS at {breakEvent.Timestamp:O} reversed within {updatedCandlesSince} candle(s)."));
                _pendingBosForTrapCheck.RemoveAt(i);
            }
            else if (updatedCandlesSince >= _thresholds.TrapReversalLookaheadCandles)
            {
                _pendingBosForTrapCheck.RemoveAt(i); // window expired without reversal — not a trap
            }
            else
            {
                _pendingBosForTrapCheck[i] = (breakEvent, updatedCandlesSince);
            }
        }
    }

    private void DetectFalseBreakout(Candle bar, bool isRanging, IProcessingContext ctx)
    {
        if (!isRanging) return;
        if (!ctx.TryGet<IReadOnlyList<SwingPoint>>("Structure.AllSwings", out var swings) || swings is not { Count: > 0 }) return;

        var unbrokenHigh = swings.LastOrDefault(s => s.Type == SwingType.High && !s.IsBroken);
        var unbrokenLow = swings.LastOrDefault(s => s.Type == SwingType.Low && !s.IsBroken);

        if (unbrokenHigh is not null && bar.High > unbrokenHigh.Price && bar.Close <= unbrokenHigh.Price)
        {
            _events.Add(new SmcEvent(_symbolId, _timeframe, bar.Timestamp, SmcEventKind.FalseBreakout, $"Exceeded range high {unbrokenHigh.Price} intra-candle, closed back inside."));
        }

        if (unbrokenLow is not null && bar.Low < unbrokenLow.Price && bar.Close >= unbrokenLow.Price)
        {
            _events.Add(new SmcEvent(_symbolId, _timeframe, bar.Timestamp, SmcEventKind.FailedBreakdown, $"Exceeded range low {unbrokenLow.Price} intra-candle, closed back inside."));
        }
    }

    private void DetectVolumeAbsorption(Candle bar, IProcessingContext ctx, decimal? volumeSma)
    {
        if (volumeSma is not > 0) return;

        var volumeMultiple = bar.Volume / volumeSma.Value;
        if (volumeMultiple < _thresholds.VolumeAbsorptionMultiple) return;

        var range = bar.High - bar.Low;
        if (range == 0) return;
        var bodyPercent = Math.Abs(bar.Close - bar.Open) / range * 100m;
        if (bodyPercent >= _thresholds.VolumeAbsorptionMaxBodyPercent) return;

        var atMarkedLevel = ctx.TryGet<IReadOnlyList<StructureZone>>("Structure.ActiveZones", out var zones)
            && zones is not null
            && zones.Any(z => bar.Low <= z.UpperBound && bar.High >= z.LowerBound);

        if (!atMarkedLevel)
        {
            atMarkedLevel = ctx.TryGet<IReadOnlyList<SwingPoint>>("Structure.AllSwings", out var swings)
                && swings is not null
                && swings.Any(s => !s.IsBroken && s.Price >= bar.Low && s.Price <= bar.High);
        }

        if (atMarkedLevel)
        {
            _events.Add(new SmcEvent(_symbolId, _timeframe, bar.Timestamp, SmcEventKind.VolumeAbsorption,
                $"Volume {volumeMultiple:F1}x SMA, Body% {bodyPercent:F1}, at marked level."));
        }
    }

    private void DetectExhaustionCandle(Candle bar, decimal? atr, TrendDirection prevailingTrend)
    {
        if (atr is not > 0) return;

        var range = bar.High - bar.Low;
        if (range < _thresholds.ExhaustionAtrMultiple * atr.Value) return;
        if (range == 0) return;

        var closeLocationInRange = (bar.Close - bar.Low) / range; // 0 = at Low, 1 = at High
        var outerFraction = _thresholds.ExhaustionOuterRangePercent / 100m;

        // "Against the prevailing trend": in an uptrend, exhaustion shows as a close
        // in the outer LOW band (rejection of higher prices); in a downtrend, the
        // outer HIGH band (rejection of lower prices).
        var isExhaustion = prevailingTrend switch
        {
            TrendDirection.Bullish => closeLocationInRange <= outerFraction,
            TrendDirection.Bearish => closeLocationInRange >= (1m - outerFraction),
            _ => false
        };

        if (isExhaustion)
        {
            _events.Add(new SmcEvent(_symbolId, _timeframe, bar.Timestamp, SmcEventKind.ExhaustionCandle,
                $"Range {range:F2} ({range / atr.Value:F1}x ATR), close location {closeLocationInRange:P0}, against {prevailingTrend} trend."));
        }
    }

    private void DetectGaps(Candle bar, PriceLeg? completedLeg, bool isRanging, TrendDirection prevailingTrend)
    {
        if (_previousCandle is null) return;

        var gap = bar.Open - _previousCandle.Close;
        if (gap == 0) return;

        var gapDirection = gap > 0 ? TrendDirection.Bullish : TrendDirection.Bearish;

        // Breakaway: gap at the start of an impulse leg that breaks out of a prior Range.
        if (completedLeg is { Kind: LegKind.Impulse } leg && leg.Direction == gapDirection && isRanging)
        {
            _events.Add(new SmcEvent(_symbolId, _timeframe, bar.Timestamp, SmcEventKind.GapBreakaway, $"Gap {gap:F2} at impulse-leg start, breaking prior range."));
            return;
        }

        // Continuation: gap mid-trend in the direction of the prevailing trend, not at a Range boundary.
        if (!isRanging && gapDirection == prevailingTrend && prevailingTrend != TrendDirection.Ranging)
        {
            _events.Add(new SmcEvent(_symbolId, _timeframe, bar.Timestamp, SmcEventKind.GapContinuation, $"Gap {gap:F2} mid-trend, direction matches prevailing {prevailingTrend} trend."));
            return;
        }

        // Exhaustion candidate: gap late in an established trend (>=3 same-direction
        // trend legs). Confirmation (CHoCH within K candles) happens in
        // CheckPendingGapExhaustion on this and subsequent bars.
        if (gapDirection == prevailingTrend && _sameDirectionTrendLegRun >= _thresholds.GapExhaustionMinPriorTrendLegs)
        {
            _pendingGapExhaustionChecks.Add((bar.Timestamp, 0));
        }
    }

    private void CheckPendingGapExhaustion(DateTimeOffset currentBarTimestamp, StructureBreakEvent? newBreak)
    {
        for (var i = _pendingGapExhaustionChecks.Count - 1; i >= 0; i--)
        {
            var (gapTimestamp, candlesSince) = _pendingGapExhaustionChecks[i];
            var updatedCandlesSince = candlesSince + 1;

            if (newBreak is { Kind: StructureBreakKind.CHoCH })
            {
                _events.Add(new SmcEvent(_symbolId, _timeframe, currentBarTimestamp, SmcEventKind.GapExhaustion,
                    $"Gap at {gapTimestamp:O} followed by CHoCH within {updatedCandlesSince} candle(s)."));
                _pendingGapExhaustionChecks.RemoveAt(i);
            }
            else if (updatedCandlesSince >= _thresholds.ExhaustionGapReversalLookaheadCandles)
            {
                _pendingGapExhaustionChecks.RemoveAt(i); // window expired without CHoCH — not exhaustion
            }
            else
            {
                _pendingGapExhaustionChecks[i] = (gapTimestamp, updatedCandlesSince);
            }
        }
    }

    private void TrackTrendLegRun(PriceLeg? completedLeg)
    {
        if (completedLeg is { Kind: LegKind.Trend } leg)
        {
            if (leg.Direction == _lastTrendLegDirection)
            {
                _sameDirectionTrendLegRun++;
            }
            else
            {
                _sameDirectionTrendLegRun = 1;
                _lastTrendLegDirection = leg.Direction;
            }
        }
    }
}
