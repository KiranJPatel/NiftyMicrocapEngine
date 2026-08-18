using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Regime;

/// <summary>
/// Implements section 13's regime suppression rule: during confirmed broad-market
/// weakness (Nifty 50 Primary Trend = Bear or Strong Bear), a new long is
/// suppressed unless the setup's own confidence clears RegimeOverrideConfidence
/// (default 90, vs the normal Buy threshold of 65). Shorts are not suppressed by
/// bearish regime (the rule is direction-specific per "suppress new long signals").
/// </summary>
public sealed class RegimeFilter : IRegimeFilter
{
    private readonly DecisionEngineOptions _options;

    public RegimeFilter(IOptions<DecisionEngineOptions> options)
    {
        _options = options.Value;
    }

    public RegimeFilterResult Evaluate(BroadMarketState marketState, TrendDirection proposedDirection)
    {
        var isBroadMarketWeak = marketState.Nifty50Trend is BroadMarketTrendState.Bear or BroadMarketTrendState.StrongBear;

        if (proposedDirection == TrendDirection.Bullish && isBroadMarketWeak)
        {
            return new RegimeFilterResult(
                IsSuppressed: true,
                RequiredOverrideConfidence: _options.RegimeOverrideConfidence,
                Reason: $"Nifty 50 trend is {marketState.Nifty50Trend} as of {marketState.AsOfDate}; new long signals suppressed unless confidence >= {_options.RegimeOverrideConfidence}.");
        }

        return new RegimeFilterResult(IsSuppressed: false, RequiredOverrideConfidence: 0m, Reason: "Regime filter not triggered.");
    }
}
