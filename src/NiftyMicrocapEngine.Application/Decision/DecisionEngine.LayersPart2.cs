using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Decision;

public sealed partial class DecisionEngine
{
    /// <summary>Volatility / Opportunity — default 10 points (configurable). Favorable ATR-relative range (enough room to target without being erratic).</summary>
    private static LayerScore ScoreVolatility(DecisionEngineInput input, decimal maxPoints)
    {
        decimal score = 0m;
        var facts = new List<string>();

        var rangeExpansion = input.CandlePsychology.RangeExpansionVsAtr;
        if (rangeExpansion is >= 1.0m and <= 2.5m)
        {
            score += maxPoints * 0.6m;
            facts.Add($"Range expansion ({rangeExpansion:F1}x ATR) is in the favorable zone — neither compressed nor erratic.");
        }
        else if (rangeExpansion is > 2.5m)
        {
            score -= maxPoints * 0.2m;
            facts.Add("Range is highly extended versus ATR — elevated volatility risk.");
        }

        var histVol = input.IndicatorValues.GetValueOrDefault("HistVol_20");
        if (histVol is not null)
        {
            score += maxPoints * 0.4m;
        }

        return new LayerScore("Volatility / Opportunity", maxPoints, score, facts);
    }

    /// <summary>Candle Psychology — default 5 points (configurable). Bullish/bearish reversal or continuation patterns matching the proposed direction.</summary>
    private static LayerScore ScoreCandlePsychology(DecisionEngineInput input, decimal maxPoints)
    {
        decimal score = 0m;
        var facts = new List<string>();
        var isBullish = input.ProposedDirection == TrendDirection.Bullish;

        var bullishPatterns = new[] { CandlePatternType.EngulfingBullish, CandlePatternType.MorningStar, CandlePatternType.ThreeWhiteSoldiers };
        var bearishPatterns = new[] { CandlePatternType.EngulfingBearish, CandlePatternType.EveningStar, CandlePatternType.ThreeBlackCrows };

        var supportivePatterns = isBullish ? bullishPatterns : bearishPatterns;
        var opposingPatterns = isBullish ? bearishPatterns : bullishPatterns;

        if (input.CandlePatterns.Any(p => supportivePatterns.Contains(p.Type)))
        {
            score += maxPoints * 0.7m;
            facts.Add("A supportive candle pattern confirms the proposed direction.");
        }

        if (input.CandlePatterns.Any(p => opposingPatterns.Contains(p.Type)))
        {
            score -= maxPoints * 0.5m;
            facts.Add("A candle pattern opposing the proposed direction was also detected.");
        }

        if (input.CandlePatterns.Any(p => p.Type == CandlePatternType.Doji))
        {
            facts.Add("Doji present — indecision at current levels.");
        }

        return new LayerScore("Candle Psychology", maxPoints, score, facts);
    }

    /// <summary>Support/Resistance proximity — default 5 points (configurable). Price near an active demand/supply zone or order block in the proposed direction.</summary>
    private static LayerScore ScoreSupportResistance(DecisionEngineInput input, decimal maxPoints)
    {
        decimal score = 0m;
        var facts = new List<string>();
        var isBullish = input.ProposedDirection == TrendDirection.Bullish;

        var relevantZoneKinds = isBullish
            ? new[] { ZoneKind.DemandZone, ZoneKind.OrderBlockBullish }
            : new[] { ZoneKind.SupplyZone, ZoneKind.OrderBlockBearish };

        var freshRelevantZone = input.PrimaryStructureSnapshot.ActiveZones
            .Any(z => relevantZoneKinds.Contains(z.Kind) && z.Status is ZoneStatus.Fresh or ZoneStatus.PartiallyMitigated);

        if (freshRelevantZone)
        {
            score += maxPoints * 0.8m;
            facts.Add(isBullish ? "Price is near an active demand zone/bullish order block." : "Price is near an active supply zone/bearish order block.");
        }

        return new LayerScore("Support/Resistance", maxPoints, score, facts);
    }

    /// <summary>Relative Strength & Regime alignment — default 5 points (configurable). Symbol outperforming its benchmark, and broad-market regime not opposing the trade.</summary>
    private static LayerScore ScoreRelativeStrengthRegime(DecisionEngineInput input, decimal maxPoints)
    {
        decimal score = 0m;
        var facts = new List<string>();
        var isBullish = input.ProposedDirection == TrendDirection.Bullish;

        var rsShort = input.RelativeStrength.ReturnRatioVsMicrocap250Short;
        if (rsShort is not null)
        {
            var outperforming = isBullish ? rsShort > 1m : rsShort < 1m;
            if (outperforming)
            {
                score += maxPoints * 0.6m;
                facts.Add($"Relative strength vs Nifty Microcap 250 ({rsShort:F2}) favors the proposed direction.");
            }
            else
            {
                score -= maxPoints * 0.3m;
                facts.Add("Relative strength vs Nifty Microcap 250 does not favor the proposed direction.");
            }
        }

        if (!input.RegimeResult.IsSuppressed)
        {
            score += maxPoints * 0.4m;
        }
        else
        {
            facts.Add("Regime filter is active — see hard gate detail.");
        }

        return new LayerScore("Relative Strength & Regime", maxPoints, score, facts);
    }
}
