using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Decision;

public enum DecisionOutcome { StrongBuy, Buy, Watch, Hold, Sell, StrongSellExit, NoTrade }

public enum HardGateKind { DataQuality, CircuitLocked, RegimeSuppressed, StructureBreakAgainstDirection }

/// <summary>
/// One hard gate's evaluation result. IMPORTANT: any Passed == false gate must
/// short-circuit the Decision Engine's Outcome to NoTrade, independent of the
/// weighted score — this is the audited fix for the failure mode where a gate
/// was originally implemented as a downweight instead of a short-circuit. See
/// IDecisionEngine's doc comment for the precise ordering requirement.
/// </summary>
public sealed record HardGateResult(HardGateKind Kind, bool Passed, string Reason);

/// <summary>
/// One scoring layer's signed sub-score within its own point budget. A layer's
/// contribution can go negative within its own allocation (e.g. late-trend
/// exhaustion subtracts from Trend's budget) — this is additive and auditable,
/// never a separate bolt-on penalty applied after the fact.
/// </summary>
public sealed record LayerScore(string LayerName, decimal MaxPoints, decimal Contribution, IReadOnlyList<string> ReasoningFacts)
{
    public decimal ClampedContribution => Math.Clamp(Contribution, -MaxPoints, MaxPoints);
}

/// <summary>
/// The full output of the Decision Engine for one symbol on one as-of date.
/// </summary>
public sealed record DecisionEngineResult(
    int SymbolId,
    DateOnly AsOfDate,
    DecisionOutcome Outcome,
    decimal ConfidenceScore,
    IReadOnlyList<HardGateResult> HardGates,
    IReadOnlyList<LayerScore> LayerScores,
    string ReasoningText,
    HardGateKind? HardGateFailed);
