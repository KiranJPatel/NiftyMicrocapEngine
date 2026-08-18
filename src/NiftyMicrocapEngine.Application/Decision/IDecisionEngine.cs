using NiftyMicrocapEngine.Application.MultiTimeframe;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Decision;

/// <summary>
/// Everything the Decision Engine needs for one symbol/as-of-date evaluation,
/// gathered by the caller (Scanner, section 17) from the structure engine,
/// indicator pipeline, MTF engine, and regime filter — the Decision Engine
/// itself performs no data fetching or indicator computation.
/// </summary>
public sealed record DecisionEngineInput(
    int SymbolId,
    DateOnly AsOfDate,
    TrendDirection ProposedDirection,
    StructureSnapshot PrimaryStructureSnapshot,
    IReadOnlyDictionary<string, decimal?> IndicatorValues,
    MtfAlignmentResult MtfAlignment,
    RegimeFilterResult RegimeResult,
    RelativeStrengthResult RelativeStrength,
    CandlePsychologyMetrics CandlePsychology,
    IReadOnlyList<CandlePatternMatch> CandlePatterns,
    bool DataQualityPassed,
    string? DataQualityFailureReason,
    bool IsCircuitLockedAgainstDirection,
    bool HasStructureBreakAgainstDirectionWithinLookback,
    string? StructureBreakAgainstDirectionDetail);

/// <summary>
/// Two-stage evaluation per build spec section 14: hard gates first, then
/// weighted scoring. CRITICAL ORDERING REQUIREMENT (the audited fix): all hard
/// gates MUST be evaluated and checked for any failure BEFORE the weighted
/// score is mapped to a DecisionOutcome. A failed gate forces Outcome = NoTrade
/// regardless of how high the weighted score would otherwise be — this is a
/// short-circuit, not a downweight applied to the score. Implementations must
/// never compute the Outcome from ConfidenceScore alone without first checking
/// HardGates for any Passed == false.
/// </summary>
public interface IDecisionEngine
{
    DecisionEngineResult Evaluate(DecisionEngineInput input);
}
