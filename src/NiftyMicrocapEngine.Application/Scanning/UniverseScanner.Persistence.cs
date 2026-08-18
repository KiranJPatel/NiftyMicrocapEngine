using Microsoft.Extensions.Logging;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Scanning;

public sealed partial class UniverseScanner
{
    /// <summary>
    /// Persists the latest indicator readings and this run's newly-detected
    /// structure events for one symbol/timeframe pipeline. Called once per
    /// symbol per stage after the pipeline has run, using the same
    /// StructureAnalysisPipelineFactory.Handles the Decision Engine input was
    /// built from — no separate re-computation.
    ///
    /// SCHEMA NOTE (RESOLVED): StructureEventType originally predated several
    /// SMC event kinds this engine's structure layer actually detects
    /// (BullTrap, BearTrap, FalseBreakout, FailedBreakdown, VolumeAbsorption,
    /// the three Gap kinds) — those fell through MapSmcEventKind's default
    /// case and were logged at Debug instead of persisted. StructureEventType
    /// (Domain/SupportingRecords.cs) now has a member for each of them, and
    /// MapSmcEventKind below is a full 1:1 mapping — no SmcEventKind should
    /// reach the `_ => null` branch anymore. SupplyZone/DemandZone remain
    /// intentionally unmapped: those are ZoneKind values from SmcZoneDetector
    /// (a different concept — a price zone, not a point-in-time event) and
    /// were never event kinds in the first place.
    /// </summary>
    private async Task PersistPipelineOutputAsync(
        int symbolId,
        Timeframe timeframe,
        DateTimeOffset asOfTimestamp,
        StructureAnalysisPipelineFactory.Handles handles,
        IIndicatorValueRepository indicatorRepo,
        IMarketStructureEventRepository structureEventRepo,
        CancellationToken ct)
    {
        // Previously hardcoded to just Atr and VolumeSma. Now that Handles
        // exposes every Phase-1 indicator plus (as of this pass) every
        // Phase-2 extended indicator via AllIndicators (see
        // StructureAnalysisPipelineFactory.Handles.SnapshotIndicatorValues's
        // doc comment), this persists all of them — IndicatorValues is a
        // key-value table specifically so this is purely additive with no
        // schema change. This is also what makes the Phase-2 extended set
        // (WMA, DEMA, TEMA, KAMA, Ichimoku, etc.) actually visible anywhere
        // outside a live scan: they're computed and persisted here even
        // though DecisionEngine.LayersPart1/2 doesn't read any of their
        // keys yet — the two are deliberately decoupled (see
        // ExtendedTrendIndicators.cs's doc comment).
        var snapshots = handles.AllIndicators
            .Select(indicator => new IndicatorSnapshot(symbolId, timeframe, asOfTimestamp, indicator.Key, indicator.CurrentValue, indicator.SignalState))
            .ToList();

        await indicatorRepo.SaveBatchAsync(snapshots, ct);

        var recentBreak = handles.StructureBreaks.Breaks.LastOrDefault(b => b.Timestamp == asOfTimestamp);
        if (recentBreak is not null)
        {
            var eventType = recentBreak.Kind == StructureBreakKind.BOS ? StructureEventType.BOS : StructureEventType.CHoCH;
            await structureEventRepo.SaveAsync(new MarketStructureEvent(symbolId, timeframe, asOfTimestamp, eventType,
                $"{recentBreak.Kind} to {recentBreak.NewDirection} at {recentBreak.BreakPrice}."), ct);
        }

        var recentSwing = handles.SwingPoints.ConfirmedSwings.LastOrDefault(s => s.Timestamp == asOfTimestamp);
        if (recentSwing is not null)
        {
            var eventType = recentSwing.Type == SwingType.High ? StructureEventType.SwingHigh : StructureEventType.SwingLow;
            await structureEventRepo.SaveAsync(new MarketStructureEvent(symbolId, timeframe, asOfTimestamp, eventType,
                $"Price {recentSwing.Price}, higher/lower-than-prior: {recentSwing.IsHigherOrLower}."), ct);
        }

        foreach (var smcEvent in handles.SmcEvents.Events.Where(e => e.Timestamp == asOfTimestamp))
        {
            var mapped = MapSmcEventKind(smcEvent.Kind);
            if (mapped is null)
            {
                _logger.LogDebug(
                    "SMC event {Kind} for SymbolId={SymbolId}/{Timeframe} has no corresponding StructureEventType — not persisted. See PersistPipelineOutputAsync schema note.",
                    smcEvent.Kind, symbolId, timeframe);
                continue;
            }

            await structureEventRepo.SaveAsync(new MarketStructureEvent(symbolId, timeframe, asOfTimestamp, mapped.Value, smcEvent.Detail), ct);
        }
    }

    private static StructureEventType? MapSmcEventKind(SmcEventKind kind) => kind switch
    {
        SmcEventKind.LiquidityGrab => StructureEventType.LiquidityGrab,
        SmcEventKind.ExhaustionCandle => StructureEventType.ExhaustionCandle,
        SmcEventKind.BullTrap => StructureEventType.BullTrap,
        SmcEventKind.BearTrap => StructureEventType.BearTrap,
        SmcEventKind.FalseBreakout => StructureEventType.FalseBreakout,
        SmcEventKind.FailedBreakdown => StructureEventType.FailedBreakdown,
        SmcEventKind.VolumeAbsorption => StructureEventType.VolumeAbsorption,
        SmcEventKind.GapBreakaway => StructureEventType.GapBreakaway,
        SmcEventKind.GapContinuation => StructureEventType.GapContinuation,
        SmcEventKind.GapExhaustion => StructureEventType.GapExhaustion,
        _ => null
    };
}
