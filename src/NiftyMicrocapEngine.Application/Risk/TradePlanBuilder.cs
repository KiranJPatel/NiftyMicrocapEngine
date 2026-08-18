using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Risk;

/// <summary>
/// Implements build spec section 16.1. The ATR floor on the stop prevents an
/// unrealistically tight stop where the structural stop sits implausibly close
/// to entry in illiquid microcaps — for a long, the effective stop is whichever
/// is FURTHER from entry (more conservative) between the structural stop and
/// the ATR floor, since "prevents unrealistic tightness" means the ATR floor
/// should win exactly when the structural stop is the tighter of the two.
/// </summary>
public sealed class TradePlanBuilder : ITradePlanBuilder
{
    private const int MinimumQualifyingImpulseLegs = 3;

    private readonly decimal _atrStopMultiple;

    public TradePlanBuilder(IOptions<RiskManagerOptions> options)
    {
        _atrStopMultiple = options.Value.StopAtrMultiple;
    }

    public TradePlan Build(TradePlanRequest request)
    {
        var stopLoss = ComputeStop(request);
        var riskPerShare = Math.Abs(request.EntryPrice - stopLoss);

        if (riskPerShare <= 0)
        {
            throw new InvalidOperationException(
                $"Computed stop ({stopLoss}) yields zero or negative risk-per-share against entry ({request.EntryPrice}). " +
                "This indicates a structural-stop or ATR input error upstream — refusing to build a trade plan with an undefined R multiple.");
        }

        var (target1, target2, target3) = ComputeTargets(request, riskPerShare);

        var riskPercent = riskPerShare / request.EntryPrice;
        var rewardPerShare = Math.Abs(target1 - request.EntryPrice);
        var riskRewardRatio = riskPerShare == 0 ? 0m : rewardPerShare / riskPerShare;

        var (estimatedDuration, durationFlag) = EstimateDuration(request.TrailingImpulseLegDurations);

        return new TradePlan(
            Entry: request.EntryPrice,
            StopLoss: stopLoss,
            Target1: target1,
            Target2: target2,
            Target3: target3,
            RiskPercent: riskPercent,
            RiskRewardRatio: riskRewardRatio,
            InvalidationLevel: request.InvalidationLevelDescription,
            EstimatedDuration: estimatedDuration,
            DurationDataQualityFlag: durationFlag);
    }

    private decimal ComputeStop(TradePlanRequest request)
    {
        var atrFloorDistance = _atrStopMultiple * request.CurrentAtr14;
        var isLong = request.Direction == TrendDirection.Bullish;

        var atrFloorStop = isLong ? request.EntryPrice - atrFloorDistance : request.EntryPrice + atrFloorDistance;

        if (request.StructuralStopPrice is not { } structuralStop)
        {
            return atrFloorStop;
        }

        // See class remarks: the effective stop is whichever is FURTHER from
        // entry (wider/more conservative), matching the stated rationale rather
        // than a literal "max" on raw price values.
        return isLong
            ? Math.Min(structuralStop, atrFloorStop)
            : Math.Max(structuralStop, atrFloorStop);
    }

    private static (decimal Target1, decimal Target2, decimal Target3) ComputeTargets(TradePlanRequest request, decimal riskPerShare)
    {
        var isLong = request.Direction == TrendDirection.Bullish;

        var r1 = isLong ? request.EntryPrice + riskPerShare : request.EntryPrice - riskPerShare;
        var r2 = isLong ? request.EntryPrice + 2 * riskPerShare : request.EntryPrice - 2 * riskPerShare;
        var r3 = isLong ? request.EntryPrice + 3 * riskPerShare : request.EntryPrice - 3 * riskPerShare;

        if (request.NextResistanceOrSupportZonePrice is not { } zonePrice)
        {
            return (r1, r2, r3);
        }

        var target1 = NearerTarget(request.EntryPrice, r1, zonePrice, isLong);
        var target2 = NearerTarget(request.EntryPrice, r2, zonePrice, isLong);
        var target3 = NearerTarget(request.EntryPrice, r3, zonePrice, isLong);

        return (target1, target2, target3);
    }

    private static decimal NearerTarget(decimal entry, decimal rMultipleTarget, decimal zonePrice, bool isLong)
    {
        var rDistance = Math.Abs(rMultipleTarget - entry);
        var zoneDistance = Math.Abs(zonePrice - entry);

        if (zoneDistance >= rDistance) return rMultipleTarget;

        var zoneIsOnCorrectSide = isLong ? zonePrice > entry : zonePrice < entry;
        return zoneIsOnCorrectSide ? zonePrice : rMultipleTarget;
    }

    /// <summary>
    /// Section 16.1's duration heuristic: average impulse-leg length over the
    /// trailing 6 months, but only if at least 3 qualifying legs exist in that
    /// window — otherwise null plus the DataQualityFlag, never a fabricated
    /// global fallback (the audited fix from the original spec review).
    /// </summary>
    private static (TimeSpan? Duration, string? Flag) EstimateDuration(IReadOnlyList<TimeSpan> trailingImpulseLegDurations)
    {
        if (trailingImpulseLegDurations.Count < MinimumQualifyingImpulseLegs)
        {
            return (null, "InsufficientHistoryForDurationEstimate");
        }

        var averageTicks = (long)trailingImpulseLegDurations.Average(d => d.Ticks);
        return (TimeSpan.FromTicks(averageTicks), null);
    }
}
