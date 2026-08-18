using System.Text;

namespace NiftyMicrocapEngine.Application.Decision;

public sealed partial class DecisionEngine
{
    /// <summary>
    /// Builds the human-readable reasoning chain required by build spec section 15
    /// — a hard requirement, since it's the only way to audit whether the default
    /// layer weights behave sensibly during validation. Walks each layer's
    /// ReasoningFacts in order, then states the final confidence and outcome (or
    /// the hard-gate failure reason if short-circuited).
    /// </summary>
    private static string BuildReasoningText(
        IReadOnlyList<LayerScore> layerScores,
        decimal confidence,
        DecisionOutcome outcome,
        HardGateKind? hardGateFailed)
    {
        var sb = new StringBuilder();

        foreach (var layer in layerScores)
        {
            foreach (var fact in layer.ReasoningFacts)
            {
                sb.Append(fact);
                if (!fact.EndsWith('.')) sb.Append('.');
                sb.Append(' ');
            }
        }

        if (hardGateFailed is not null)
        {
            sb.Append($"Hard gate failed: {hardGateFailed}. Outcome forced to No Trade regardless of the {confidence:F0}% weighted confidence that would otherwise apply.");
        }
        else
        {
            sb.Append($"Confidence {confidence:F0}%. Outcome: {DescribeOutcome(outcome)}.");
        }

        return sb.ToString().Trim();
    }

    private static string DescribeOutcome(DecisionOutcome outcome) => outcome switch
    {
        DecisionOutcome.StrongBuy => "Strong Buy",
        DecisionOutcome.Buy => "Buy",
        DecisionOutcome.Watch => "Watch",
        DecisionOutcome.Hold => "Hold",
        DecisionOutcome.Sell => "Sell",
        DecisionOutcome.StrongSellExit => "Strong Sell / Exit",
        DecisionOutcome.NoTrade => "No Trade",
        _ => outcome.ToString()
    };
}
