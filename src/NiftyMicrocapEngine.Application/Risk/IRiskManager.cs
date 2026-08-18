using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Risk;

/// <summary>
/// Per-trade risk/reward plan for one candidate setup, computed only when the
/// Decision Engine's outcome is Buy or StrongBuy (or the short equivalents,
/// once shorting is in scope) — the Risk Manager does not itself decide
/// whether to trade, only how to size/exit a trade the Decision Engine already
/// approved. Matches build spec section 16.1.
/// </summary>
public sealed record TradePlan(
    decimal Entry,
    decimal StopLoss,
    decimal Target1,
    decimal Target2,
    decimal Target3,
    decimal RiskPercent,
    decimal RiskRewardRatio,
    string InvalidationLevel,
    TimeSpan? EstimatedDuration,
    string? DurationDataQualityFlag);

/// <summary>
/// The result of checking one candidate trade against portfolio-level limits
/// (section 16.2). A technically excellent setup that would breach a limit is
/// downgraded to Watch with the specific limit named — never silently promoted
/// through by the caller. This type only reports the check result; enforcing
/// the downgrade (rewriting the Decision Engine's Outcome) is the caller's job,
/// since the Risk Manager operates one level below the Decision Engine and has
/// no authority to mutate its result.
/// </summary>
public sealed record PortfolioLimitCheckResult(bool WithinLimits, IReadOnlyList<string> BreachedLimits);

public interface ITradePlanBuilder
{
    /// <summary>
    /// Builds a TradePlan for a symbol already approved (Buy/StrongBuy) by the
    /// Decision Engine. Direction-aware: for a long, StopLoss sits below Entry;
    /// for a short, above. Section 16.1's stop formula is written for longs
    /// ("below last swing low / demand zone") — the short-side mirror (above
    /// last swing high / supply zone) is the natural symmetric extension, since
    /// the Decision Engine already supports TrendDirection.Bearish.
    /// </summary>
    TradePlan Build(TradePlanRequest request);
}

public sealed record TradePlanRequest(
    TrendDirection Direction,
    decimal EntryPrice,
    decimal? StructuralStopPrice,
    decimal CurrentAtr14,
    decimal? NextResistanceOrSupportZonePrice,
    string InvalidationLevelDescription,
    IReadOnlyList<TimeSpan> TrailingImpulseLegDurations);

public interface IPortfolioRiskManager
{
    PortfolioLimitCheckResult CheckLimits(PortfolioLimitCheckRequest request);
}

public sealed record OpenPosition(int SymbolId, string Sector, decimal DeployedCapital, IReadOnlyList<decimal> Trailing60DayReturns);

public sealed record PortfolioLimitCheckRequest(
    OpenPosition CandidatePosition,
    IReadOnlyList<OpenPosition> ExistingOpenPositions,
    decimal TotalDeployedCapital);
