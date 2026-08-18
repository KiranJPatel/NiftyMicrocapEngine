using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Scanning;

public sealed partial class UniverseScanner
{
    private TradePlan BuildTradePlanFor(StructureAnalysisPipelineFactory.Handles dailyPipeline, IReadOnlyList<Candle> dailyCandles, TrendDirection direction)
    {
        var latestCandle = dailyCandles[^1];
        var isLong = direction == TrendDirection.Bullish;

        var structuralStop = isLong
            ? dailyPipeline.SwingPoints.ConfirmedSwings.LastOrDefault(s => s.Type == SwingType.Low)?.Price
            : dailyPipeline.SwingPoints.ConfirmedSwings.LastOrDefault(s => s.Type == SwingType.High)?.Price;

        var nextZone = FindNextZonePrice(dailyPipeline, latestCandle, isLong);

        var trailingImpulseLegDurations = dailyPipeline.ImpulseLegs.Legs
            .Where(l => l.Kind == LegKind.Impulse && l.EndTimestamp >= latestCandle.Timestamp.AddMonths(-6))
            .Select(l => l.EndTimestamp - l.StartTimestamp)
            .ToList();

        var invalidationDescription = structuralStop is not null
            ? "Structure break beyond " + structuralStop
            : "No confirmed structural invalidation level available";

        var request = new TradePlanRequest(
            direction,
            latestCandle.Close,
            structuralStop,
            dailyPipeline.Atr.CurrentValue ?? 0m,
            nextZone,
            invalidationDescription,
            trailingImpulseLegDurations);

        return _tradePlanBuilder.Build(request);
    }

    private static decimal? FindNextZonePrice(StructureAnalysisPipelineFactory.Handles dailyPipeline, Candle latestCandle, bool isLong)
    {
        if (isLong)
        {
            var supplyZones = dailyPipeline.SmcZones.Zones
                .Where(z => (z.Kind == ZoneKind.SupplyZone || z.Kind == ZoneKind.OrderBlockBearish))
                .Where(z => z.Status != ZoneStatus.FullyMitigated)
                .Where(z => z.LowerBound > latestCandle.Close)
                .OrderBy(z => z.LowerBound)
                .ToList();

            return supplyZones.Count > 0 ? supplyZones[0].LowerBound : (decimal?)null;
        }

        var demandZones = dailyPipeline.SmcZones.Zones
            .Where(z => (z.Kind == ZoneKind.DemandZone || z.Kind == ZoneKind.OrderBlockBullish))
            .Where(z => z.Status != ZoneStatus.FullyMitigated)
            .Where(z => z.UpperBound < latestCandle.Close)
            .OrderByDescending(z => z.UpperBound)
            .ToList();

        return demandZones.Count > 0 ? demandZones[0].UpperBound : (decimal?)null;
    }
}
