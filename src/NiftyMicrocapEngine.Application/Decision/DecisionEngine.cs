using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Decision;

public sealed partial class DecisionEngine : IDecisionEngine
{
    private readonly DecisionEngineOptions _options;

    public DecisionEngine(IOptions<DecisionEngineOptions> options)
    {
        _options = options.Value;
    }

    public DecisionEngineResult Evaluate(DecisionEngineInput input)
    {
        var hardGates = EvaluateHardGates(input);

        // AUDITED FIX: check for any gate failure BEFORE computing/using the
        // weighted score to determine Outcome. A failed gate short-circuits to
        // NoTrade — the weighted score is still computed below (for
        // explainability/audit purposes) but never used to override a failed
        // gate's NoTrade outcome, no matter how high it scores.
        var failedGate = hardGates.FirstOrDefault(g => !g.Passed);

        var layerScores = ComputeLayerScores(input, _options.LayerWeights);
        var confidence = layerScores.Sum(l => l.ClampedContribution);
        confidence = Math.Clamp(confidence, 0m, 100m);

        DecisionOutcome outcome;
        HardGateKind? hardGateFailed;

        if (failedGate is not null)
        {
            // Special case within the short-circuit: the Regime-Suppressed gate
            // is an OVERRIDABLE short-circuit — if the setup's own confidence
            // clears the required override threshold, the gate does not apply
            // and scoring proceeds normally. Still evaluated strictly BEFORE
            // outcome mapping, using the already-computed confidence, not a
            // separate re-scoring pass.
            if (failedGate.Kind == HardGateKind.RegimeSuppressed
                && confidence >= input.RegimeResult.RequiredOverrideConfidence)
            {
                outcome = MapConfidenceToOutcome(confidence);
                hardGateFailed = null;
            }
            else
            {
                outcome = DecisionOutcome.NoTrade;
                hardGateFailed = failedGate.Kind;
            }
        }
        else
        {
            outcome = MapConfidenceToOutcome(confidence);
            hardGateFailed = null;
        }
        var reasoning = BuildReasoningText(layerScores, confidence, outcome, hardGateFailed);

        return new DecisionEngineResult(
            input.SymbolId,
            input.AsOfDate,
            outcome,
            confidence,
            hardGates,
            layerScores,
            reasoning,
            hardGateFailed);
    }

    private static IReadOnlyList<HardGateResult> EvaluateHardGates(DecisionEngineInput input)
    {
        var gates = new List<HardGateResult>
        {
            new(
                HardGateKind.DataQuality,
                input.DataQualityPassed,
                input.DataQualityPassed ? "Data quality checks passed." : input.DataQualityFailureReason ?? "Data quality gate failed."),

            new(
                HardGateKind.CircuitLocked,
                !input.IsCircuitLockedAgainstDirection,
                input.IsCircuitLockedAgainstDirection
                    ? $"Symbol is circuit-locked against the proposed {input.ProposedDirection} direction."
                    : "No circuit lock against the proposed direction."),

            new(
                HardGateKind.RegimeSuppressed,
                !input.RegimeResult.IsSuppressed,
                input.RegimeResult.IsSuppressed ? input.RegimeResult.Reason : "Regime filter not triggered."),

            new(
                HardGateKind.StructureBreakAgainstDirection,
                !input.HasStructureBreakAgainstDirectionWithinLookback,
                input.HasStructureBreakAgainstDirectionWithinLookback
                    ? input.StructureBreakAgainstDirectionDetail ?? "Confirmed structure break against the proposed direction on the primary timeframe within the lookback window."
                    : "No structure break against the proposed direction within the lookback window.")
        };

        return gates;
    }

    private DecisionOutcome MapConfidenceToOutcome(decimal confidence)
    {
        var t = _options.Thresholds;
        return confidence switch
        {
            _ when confidence >= t.StrongBuy => DecisionOutcome.StrongBuy,
            _ when confidence >= t.Buy => DecisionOutcome.Buy,
            _ when confidence >= t.Watch => DecisionOutcome.Watch,
            _ when confidence >= t.Hold => DecisionOutcome.Hold,
            _ when confidence >= t.Sell => DecisionOutcome.Sell,
            _ => DecisionOutcome.StrongSellExit
        };
    }
}
