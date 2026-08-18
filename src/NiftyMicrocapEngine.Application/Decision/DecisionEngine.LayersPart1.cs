using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Decision;

public sealed partial class DecisionEngine
{
    private static List<LayerScore> ComputeLayerScores(DecisionEngineInput input, Configuration.DecisionLayerWeights weights)
    {
        return new List<LayerScore>
        {
            ScoreMarketStructure(input, weights.Structure),
            ScoreTrend(input, weights.Trend),
            ScoreMomentum(input, weights.Momentum),
            ScoreVolume(input, weights.Volume),
            ScoreVolatility(input, weights.Volatility),
            ScoreCandlePsychology(input, weights.Psychology),
            ScoreSupportResistance(input, weights.SupportResistance),
            ScoreRelativeStrengthRegime(input, weights.RelativeStrengthRegime)
        };
    }

    /// <summary>Market Structure — default 25 points (configurable). Rewards trend-confirming structure (BOS in the proposed direction, HH/HL or LH/LL sequence) and penalizes CHoCH against it.</summary>
    private static LayerScore ScoreMarketStructure(DecisionEngineInput input, decimal maxPoints)
    {
        decimal score = 0m;
        var facts = new List<string>();

        var snapshot = input.PrimaryStructureSnapshot;

        if (snapshot.PrevailingTrend == input.ProposedDirection)
        {
            score += maxPoints * 0.6m;
            facts.Add($"Primary trend is {snapshot.PrevailingTrend}, matching the proposed {input.ProposedDirection} direction.");
        }
        else if (snapshot.PrevailingTrend == TrendDirection.Ranging)
        {
            facts.Add("Primary trend is Ranging — no directional structure confirmation available.");
        }
        else
        {
            score -= maxPoints * 0.4m;
            facts.Add($"Primary trend is {snapshot.PrevailingTrend}, opposing the proposed {input.ProposedDirection} direction.");
        }

        var recentBos = snapshot.RecentBreaks.LastOrDefault(b => b.Kind == StructureBreakKind.BOS);
        if (recentBos is not null && recentBos.NewDirection == input.ProposedDirection)
        {
            score += maxPoints * 0.25m;
            facts.Add("Most recent confirmed break is a BOS in the proposed direction.");
        }

        var recentChoch = snapshot.RecentBreaks.LastOrDefault(b => b.Kind == StructureBreakKind.CHoCH);
        if (recentChoch is not null && recentChoch.NewDirection != input.ProposedDirection
            && snapshot.RecentBreaks.IndexOf(recentChoch) > snapshot.RecentBreaks.IndexOf(recentBos ?? recentChoch))
        {
            score -= maxPoints * 0.3m;
            facts.Add("A recent CHoCH occurred against the proposed direction.");
        }

        var lastTwoSwingsSameType = snapshot.RecentSwings
            .Where(s => s.Type == (input.ProposedDirection == TrendDirection.Bullish ? SwingType.High : SwingType.Low))
            .TakeLast(2)
            .ToList();

        if (lastTwoSwingsSameType.Count == 2 && lastTwoSwingsSameType[1].IsHigherOrLower)
        {
            score += maxPoints * 0.15m;
            facts.Add(input.ProposedDirection == TrendDirection.Bullish
                ? "Higher-high sequence confirmed."
                : "Lower-low sequence confirmed.");
        }

        return new LayerScore("Market Structure", maxPoints, score, facts);
    }

    /// <summary>Trend — default 20 points (configurable). Rewards price above/below key moving averages and ADX confirmation; subtracts for late-trend exhaustion within its own budget.</summary>
    private static LayerScore ScoreTrend(DecisionEngineInput input, decimal maxPoints)
    {
        decimal score = 0m;
        var facts = new List<string>();
        var isBullish = input.ProposedDirection == TrendDirection.Bullish;

        var ema20 = input.IndicatorValues.GetValueOrDefault("EMA_20");
        var ema50 = input.IndicatorValues.GetValueOrDefault("EMA_50");

        if (ema20 is not null && ema50 is not null)
        {
            var emaAligned = isBullish ? ema20 > ema50 : ema20 < ema50;
            if (emaAligned)
            {
                score += maxPoints * 0.4m;
                facts.Add(isBullish ? "20 EMA above 50 EMA." : "20 EMA below 50 EMA.");
            }
            else
            {
                score -= maxPoints * 0.2m;
                facts.Add("Short/medium EMA alignment opposes the proposed direction.");
            }
        }

        var adx = input.IndicatorValues.GetValueOrDefault("ADX_14");
        if (adx is > 25m)
        {
            score += maxPoints * 0.3m;
            facts.Add($"ADX({adx:F1}) confirms a trending regime.");
        }
        else if (adx is not null)
        {
            facts.Add($"ADX({adx:F1}) below 25 — trend strength inconclusive.");
        }

        // Late-trend exhaustion subtracts from THIS layer's own budget — additive
        // and auditable, not a separate bolt-on penalty layer (§14's requirement).
        var hasExhaustion = input.PrimaryStructureSnapshot.RecentSmcEvents.Any(e => e.Kind == SmcEventKind.ExhaustionCandle);
        if (hasExhaustion)
        {
            score -= maxPoints * 0.25m;
            facts.Add("Recent exhaustion candle detected — late-trend caution applied within the Trend layer's own budget.");
        }
        else
        {
            score += maxPoints * 0.3m;
        }

        return new LayerScore("Trend", maxPoints, score, facts);
    }

    /// <summary>Momentum — default 15 points (configurable). RSI/MACD/Stochastic agreement with proposed direction.</summary>
    private static LayerScore ScoreMomentum(DecisionEngineInput input, decimal maxPoints)
    {
        decimal score = 0m;
        var facts = new List<string>();
        var isBullish = input.ProposedDirection == TrendDirection.Bullish;

        var rsi = input.IndicatorValues.GetValueOrDefault("RSI_14");
        if (rsi is not null)
        {
            var rsiSupportive = isBullish ? rsi >= 50m : rsi <= 50m;
            if (rsiSupportive)
            {
                score += maxPoints * 0.4m;
                facts.Add($"RSI({rsi:F0}) supports the proposed direction.");
            }
            else
            {
                score -= maxPoints * 0.2m;
                facts.Add($"RSI({rsi:F0}) opposes the proposed direction.");
            }
        }

        var macd = input.IndicatorValues.GetValueOrDefault("MACD_12_26_9");
        if (macd is not null)
        {
            var macdSupportive = isBullish ? macd > 0m : macd < 0m;
            if (macdSupportive)
            {
                score += maxPoints * 0.35m;
                facts.Add("MACD confirms momentum in the proposed direction.");
            }
        }

        var stochastic = input.IndicatorValues.GetValueOrDefault("Stochastic_14_3");
        if (stochastic is not null)
        {
            var stochOverextended = isBullish ? stochastic >= 80m : stochastic <= 20m;
            if (stochOverextended)
            {
                score -= maxPoints * 0.15m;
                facts.Add("Stochastic is in overextended territory — momentum may be due for a pause.");
            }
            else
            {
                score += maxPoints * 0.25m;
            }
        }

        return new LayerScore("Momentum", maxPoints, score, facts);
    }

    /// <summary>Volume — default 15 points (configurable). Volume expansion on moves in the proposed direction, absorption/spike SMC events.</summary>
    private static LayerScore ScoreVolume(DecisionEngineInput input, decimal maxPoints)
    {
        decimal score = 0m;
        var facts = new List<string>();

        var relativeVolume = input.CandlePsychology.RelativeVolume;
        if (relativeVolume is > 1.5m)
        {
            score += maxPoints * 0.5m;
            facts.Add($"Volume expanded {relativeVolume:F1}x versus the 20-period average.");
        }
        else if (relativeVolume is < 0.7m)
        {
            score -= maxPoints * 0.2m;
            facts.Add("Volume is well below average — weak participation.");
        }

        var hasVolumeAbsorption = input.PrimaryStructureSnapshot.RecentSmcEvents.Any(e => e.Kind == SmcEventKind.VolumeAbsorption);
        if (hasVolumeAbsorption)
        {
            score -= maxPoints * 0.3m;
            facts.Add("Volume absorption detected at a marked level — possible distribution/accumulation against the move.");
        }

        var obv = input.IndicatorValues.GetValueOrDefault("OBV");
        if (obv is not null)
        {
            score += maxPoints * 0.2m;
        }

        return new LayerScore("Volume", maxPoints, score, facts);
    }
}
