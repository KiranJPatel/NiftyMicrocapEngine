using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Structure;

/// <summary>
/// Detects Order Blocks, Fair Value Gaps, and Supply/Demand zones per build spec §9.
/// Order Blocks and Supply/Demand zones both key off "impulse leg" (§8), so this runs
/// after ImpulseLegClassifier (-180) in the pipeline. FVG detection is independent
/// (pure 3-candle geometry) and could run standalone, but is kept in the same
/// processor since both populate the same ActiveZones output consumed downstream.
///
/// Rule recap (§9):
/// Order Block (bullish): last down-close candle immediately preceding an impulse
/// leg that breaks structure upward.
/// Order Block (bearish): last up-close candle immediately preceding an impulse leg
/// that breaks structure downward.
/// Fair Value Gap (bullish): candle1.High is less than candle3.Low; gap zone is
/// [candle1.High, candle3.Low].
/// Fair Value Gap (bearish): candle1.Low is greater than candle3.High; gap zone is
/// [candle3.High, candle1.Low].
/// Supply/Demand zone: the origin candle range of an impulse leg (same impulse
/// definition as Order Block).
///
/// Zone mitigation (Fresh -> PartiallyMitigated -> FullyMitigated) is tracked here
/// too: a zone is mitigated as price trades back into its range on a later candle.
/// </summary>
public sealed class SmcZoneDetector : IBarProcessor
{
    private readonly int _symbolId;
    private readonly Timeframe _timeframe;

    private readonly List<Candle> _recentCandles = new(3);
    private Candle? _previousCandle;
    private readonly List<StructureZone> _zones = new();

    public SmcZoneDetector(int symbolId, Timeframe timeframe)
    {
        _symbolId = symbolId;
        _timeframe = timeframe;
    }

    public int Priority => -170; // after ImpulseLegClassifier (-180)

    public IReadOnlyList<StructureZone> Zones => _zones;

    public Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct)
    {
        // --- Update mitigation status of existing zones against this newly-closed candle. ---
        for (var i = 0; i < _zones.Count; i++)
        {
            _zones[i] = UpdateMitigation(_zones[i], bar);
        }

        // --- FVG detection: needs a trailing 3-candle window. ---
        _recentCandles.Add(bar);
        if (_recentCandles.Count > 3) _recentCandles.RemoveAt(0);

        if (_recentCandles.Count == 3)
        {
            var c1 = _recentCandles[0];
            var c3 = _recentCandles[2];

            if (c1.High < c3.Low)
            {
                _zones.Add(new StructureZone(_symbolId, _timeframe, ZoneKind.FvgBullish, bar.Timestamp, c3.Low, c1.High));
            }
            else if (c1.Low > c3.High)
            {
                _zones.Add(new StructureZone(_symbolId, _timeframe, ZoneKind.FvgBearish, bar.Timestamp, c1.Low, c3.High));
            }
        }

        // --- Order Block / Supply-Demand zone: triggered by a newly-completed impulse leg. ---
        // NOTE: this processor keeps only a 1-candle lookback (_previousCandle), which
        // is used as the "origin candle" of the impulse leg per §9. Since legs are
        // only classified retrospectively once the next swing confirms, the true
        // "candle immediately preceding the leg's start" is approximated here as the
        // candle seen immediately before this leg was recorded as completed. This is
        // accurate for the common case but callers needing the exact origin candle
        // against arbitrary history should cross-reference the leg's StartTimestamp
        // against the persisted candle repository rather than relying solely on this
        // processor's in-memory lookback.
        if (ctx.TryGet<PriceLeg?>("Structure.CompletedLeg", out var completedLeg) && completedLeg is { Kind: LegKind.Impulse } leg
            && _previousCandle is not null)
        {
            if (leg.Direction == TrendDirection.Bullish)
            {
                _zones.Add(new StructureZone(_symbolId, _timeframe, ZoneKind.SupplyZone, leg.StartTimestamp,
                    Math.Max(_previousCandle.Open, _previousCandle.Close), Math.Min(_previousCandle.Open, _previousCandle.Close)));

                if (_previousCandle.Close < _previousCandle.Open) // last down-close candle preceding the up-impulse
                {
                    _zones.Add(new StructureZone(_symbolId, _timeframe, ZoneKind.OrderBlockBullish, leg.StartTimestamp,
                        _previousCandle.High, _previousCandle.Low));
                }
            }
            else if (leg.Direction == TrendDirection.Bearish)
            {
                _zones.Add(new StructureZone(_symbolId, _timeframe, ZoneKind.DemandZone, leg.StartTimestamp,
                    Math.Max(_previousCandle.Open, _previousCandle.Close), Math.Min(_previousCandle.Open, _previousCandle.Close)));

                if (_previousCandle.Close > _previousCandle.Open) // last up-close candle preceding the down-impulse
                {
                    _zones.Add(new StructureZone(_symbolId, _timeframe, ZoneKind.OrderBlockBearish, leg.StartTimestamp,
                        _previousCandle.High, _previousCandle.Low));
                }
            }
        }

        _previousCandle = bar;
        ctx.Set("Structure.ActiveZones", (IReadOnlyList<StructureZone>)_zones.Where(z => z.Status != ZoneStatus.Invalidated && z.Status != ZoneStatus.FullyMitigated).ToList());

        return Task.CompletedTask;
    }

    private static StructureZone UpdateMitigation(StructureZone zone, Candle bar)
    {
        if (zone.Status is ZoneStatus.FullyMitigated or ZoneStatus.Invalidated) return zone;

        var tradedIntoZone = bar.Low <= zone.UpperBound && bar.High >= zone.LowerBound;
        if (!tradedIntoZone) return zone;

        var fullyCovered = bar.Low <= zone.LowerBound && bar.High >= zone.UpperBound;
        var newStatus = fullyCovered ? ZoneStatus.FullyMitigated : ZoneStatus.PartiallyMitigated;
        var mitigatedTimestamp = zone.MitigatedTimestamp ?? bar.Timestamp;

        return zone with { Status = newStatus, MitigatedTimestamp = mitigatedTimestamp };
    }
}
