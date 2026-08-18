using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Structure;

/// <summary>
/// Implements every rule in build spec §10's pattern table, plus the always-computed
/// per-candle metrics. Stateless/pure — takes an ordered candle window and returns
/// results for the most recent candle(s); does not itself maintain rolling state
/// (unlike the IIndicator implementations), since patterns are evaluated fresh each
/// time against a fixed lookback window rather than incrementally accumulated.
/// </summary>
public sealed class CandlePsychologyAnalyzer : ICandlePsychologyAnalyzer
{
    private readonly CandlePsychologyThresholds _thresholds;

    public CandlePsychologyAnalyzer(CandlePsychologyThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? new CandlePsychologyThresholds();
    }

    public CandlePsychologyMetrics ComputeMetrics(IReadOnlyList<Candle> orderedCandles, decimal? currentAtr, decimal? volumeSma20)
    {
        if (orderedCandles.Count == 0)
            throw new ArgumentException("At least one candle is required.", nameof(orderedCandles));

        var candle = orderedCandles[^1];
        var range = candle.High - candle.Low;
        var body = Math.Abs(candle.Close - candle.Open);
        var upperWick = candle.High - Math.Max(candle.Open, candle.Close);
        var lowerWick = Math.Min(candle.Open, candle.Close) - candle.Low;

        var bodyPercent = range == 0 ? 0m : body / range * 100m;
        var upperWickPercent = range == 0 ? 0m : upperWick / range * 100m;
        var lowerWickPercent = range == 0 ? 0m : lowerWick / range * 100m;

        decimal? rangeExpansionVsAtr = currentAtr is > 0 ? range / currentAtr.Value : null;
        decimal? relativeVolume = volumeSma20 is > 0 ? candle.Volume / volumeSma20.Value : null;

        // Close location within range: 0 = at the Low, 1 = at the High, 0.5 = midpoint.
        var closeLocationInRange = range == 0 ? 0.5m : (candle.Close - candle.Low) / range;

        return new CandlePsychologyMetrics(
            BodyPercent: bodyPercent,
            UpperWickPercent: upperWickPercent,
            LowerWickPercent: lowerWickPercent,
            RangeExpansionVsAtr: rangeExpansionVsAtr,
            RelativeVolume: relativeVolume,
            CloseLocationInRange: closeLocationInRange);
    }

    public IReadOnlyList<CandlePatternMatch> DetectPatterns(IReadOnlyList<Candle> orderedCandles)
    {
        if (orderedCandles.Count == 0) return Array.Empty<CandlePatternMatch>();

        var matches = new List<CandlePatternMatch>();
        var last = orderedCandles[^1];
        var lastMetrics = SingleCandleMetrics(last);

        // --- Single-candle patterns ---
        if (lastMetrics.BodyPercent < _thresholds.DojiMaxBodyPercent)
            matches.Add(new CandlePatternMatch(CandlePatternType.Doji, last.Timestamp));

        if (lastMetrics.BodyPercent > _thresholds.MarubozuMinBodyPercent)
            matches.Add(new CandlePatternMatch(CandlePatternType.Marubozu, last.Timestamp));

        if (IsPinBar(lastMetrics))
            matches.Add(new CandlePatternMatch(CandlePatternType.PinBar, last.Timestamp));

        // --- Two-candle patterns ---
        if (orderedCandles.Count >= 2)
        {
            var prev = orderedCandles[^2];

            if (IsBullishEngulfing(prev, last)) matches.Add(new CandlePatternMatch(CandlePatternType.EngulfingBullish, last.Timestamp));
            if (IsBearishEngulfing(prev, last)) matches.Add(new CandlePatternMatch(CandlePatternType.EngulfingBearish, last.Timestamp));
            if (IsHarami(prev, last)) matches.Add(new CandlePatternMatch(CandlePatternType.Harami, last.Timestamp));
            if (IsInsideBar(prev, last)) matches.Add(new CandlePatternMatch(CandlePatternType.InsideBar, last.Timestamp));
            if (IsOutsideBar(prev, last)) matches.Add(new CandlePatternMatch(CandlePatternType.OutsideBar, last.Timestamp));
        }

        // --- Three-candle patterns ---
        if (orderedCandles.Count >= 3)
        {
            var c1 = orderedCandles[^3];
            var c2 = orderedCandles[^2];
            var c3 = last;

            if (IsMorningStar(c1, c2, c3)) matches.Add(new CandlePatternMatch(CandlePatternType.MorningStar, last.Timestamp));
            if (IsEveningStar(c1, c2, c3)) matches.Add(new CandlePatternMatch(CandlePatternType.EveningStar, last.Timestamp));
            if (IsThreeWhiteSoldiers(c1, c2, c3)) matches.Add(new CandlePatternMatch(CandlePatternType.ThreeWhiteSoldiers, last.Timestamp));
            if (IsThreeBlackCrows(c1, c2, c3)) matches.Add(new CandlePatternMatch(CandlePatternType.ThreeBlackCrows, last.Timestamp));
        }

        return matches;
    }

    private (decimal BodyPercent, decimal UpperWickPercent, decimal LowerWickPercent, decimal Range) SingleCandleMetrics(Candle c)
    {
        var range = c.High - c.Low;
        var body = Math.Abs(c.Close - c.Open);
        var upperWick = c.High - Math.Max(c.Open, c.Close);
        var lowerWick = Math.Min(c.Open, c.Close) - c.Low;

        return range == 0
            ? (0m, 0m, 0m, 0m)
            : (body / range * 100m, upperWick / range * 100m, lowerWick / range * 100m, range);
    }

    /// <summary>Pin bar: one wick > 66% of range, Body% < 33%, opposite wick small (&lt;10% of range) — §10.</summary>
    private bool IsPinBar((decimal BodyPercent, decimal UpperWickPercent, decimal LowerWickPercent, decimal Range) m)
    {
        if (m.Range == 0 || m.BodyPercent >= _thresholds.PinBarMaxBodyPercent) return false;

        var upperIsLongWick = m.UpperWickPercent > _thresholds.PinBarMinWickPercent && m.LowerWickPercent < _thresholds.PinBarMaxOppositeWickPercent;
        var lowerIsLongWick = m.LowerWickPercent > _thresholds.PinBarMinWickPercent && m.UpperWickPercent < _thresholds.PinBarMaxOppositeWickPercent;

        return upperIsLongWick || lowerIsLongWick;
    }

    private static bool IsBullish(Candle c) => c.Close > c.Open;
    private static bool IsBearish(Candle c) => c.Close < c.Open;
    private static (decimal Low, decimal High) BodyRange(Candle c) => (Math.Min(c.Open, c.Close), Math.Max(c.Open, c.Close));

    /// <summary>Engulfing: candle2 body fully contains candle1 body and is the opposite color — §10.</summary>
    private static bool IsBullishEngulfing(Candle c1, Candle c2)
    {
        if (!IsBearish(c1) || !IsBullish(c2)) return false;
        var (c1Low, c1High) = BodyRange(c1);
        var (c2Low, c2High) = BodyRange(c2);
        return c2Low <= c1Low && c2High >= c1High;
    }

    private static bool IsBearishEngulfing(Candle c1, Candle c2)
    {
        if (!IsBullish(c1) || !IsBearish(c2)) return false;
        var (c1Low, c1High) = BodyRange(c1);
        var (c2Low, c2High) = BodyRange(c2);
        return c2Low <= c1Low && c2High >= c1High;
    }

    /// <summary>Harami: candle2 body fully inside candle1 body, opposite color — §10.</summary>
    private static bool IsHarami(Candle c1, Candle c2)
    {
        var oppositeColor = (IsBullish(c1) && IsBearish(c2)) || (IsBearish(c1) && IsBullish(c2));
        if (!oppositeColor) return false;

        var (c1Low, c1High) = BodyRange(c1);
        var (c2Low, c2High) = BodyRange(c2);
        return c2Low >= c1Low && c2High <= c1High;
    }

    /// <summary>Inside bar: candle2's High/Low range fully inside candle1's range — §10.</summary>
    private static bool IsInsideBar(Candle c1, Candle c2) => c2.High <= c1.High && c2.Low >= c1.Low;

    /// <summary>Outside bar: candle2's High/Low range fully outside (engulfs) candle1's range — §10.</summary>
    private static bool IsOutsideBar(Candle c1, Candle c2) => c2.High >= c1.High && c2.Low <= c1.Low;

    /// <summary>Morning Star: long bearish -> small body/doji gapping down -> long bullish closing beyond candle1's midpoint — §10.</summary>
    private bool IsMorningStar(Candle c1, Candle c2, Candle c3)
    {
        if (!IsBearish(c1)) return false;
        var c1Metrics = SingleCandleMetrics(c1);
        if (c1Metrics.BodyPercent <= _thresholds.MarubozuMinBodyPercent - 30m) return false; // "long" bearish body — see class remarks below

        var c2Metrics = SingleCandleMetrics(c2);
        var c2GapsDown = Math.Max(c2.Open, c2.Close) < c1.Close;
        var c2IsSmallOrDoji = c2Metrics.BodyPercent < _thresholds.DojiMaxBodyPercent * 3; // "small body/doji" — see class remarks

        if (!c2GapsDown || !c2IsSmallOrDoji) return false;

        if (!IsBullish(c3)) return false;
        var c1Midpoint = (c1.Open + c1.Close) / 2m;
        return c3.Close > c1Midpoint;
    }

    /// <summary>Evening Star: mirror of Morning Star, bullish -> small/doji gapping up -> long bearish — §10.</summary>
    private bool IsEveningStar(Candle c1, Candle c2, Candle c3)
    {
        if (!IsBullish(c1)) return false;
        var c1Metrics = SingleCandleMetrics(c1);
        if (c1Metrics.BodyPercent <= _thresholds.MarubozuMinBodyPercent - 30m) return false;

        var c2Metrics = SingleCandleMetrics(c2);
        var c2GapsUp = Math.Min(c2.Open, c2.Close) > c1.Close;
        var c2IsSmallOrDoji = c2Metrics.BodyPercent < _thresholds.DojiMaxBodyPercent * 3;

        if (!c2GapsUp || !c2IsSmallOrDoji) return false;

        if (!IsBearish(c3)) return false;
        var c1Midpoint = (c1.Open + c1.Close) / 2m;
        return c3.Close < c1Midpoint;
    }

    /// <summary>Three White Soldiers: three consecutive bullish candles, each closing higher, each Body% > 60%, no long upper wicks — §10.</summary>
    private bool IsThreeWhiteSoldiers(Candle c1, Candle c2, Candle c3)
    {
        if (!IsBullish(c1) || !IsBullish(c2) || !IsBullish(c3)) return false;
        if (!(c2.Close > c1.Close && c3.Close > c2.Close)) return false;

        var m1 = SingleCandleMetrics(c1);
        var m2 = SingleCandleMetrics(c2);
        var m3 = SingleCandleMetrics(c3);

        var allStrongBodies = m1.BodyPercent > _thresholds.ThreeSoldiersCrowsMinBodyPercent
            && m2.BodyPercent > _thresholds.ThreeSoldiersCrowsMinBodyPercent
            && m3.BodyPercent > _thresholds.ThreeSoldiersCrowsMinBodyPercent;

        var noLongUpperWicks = m1.UpperWickPercent < _thresholds.PinBarMinWickPercent
            && m2.UpperWickPercent < _thresholds.PinBarMinWickPercent
            && m3.UpperWickPercent < _thresholds.PinBarMinWickPercent;

        return allStrongBodies && noLongUpperWicks;
    }

    /// <summary>Three Black Crows: mirror, bearish — §10.</summary>
    private bool IsThreeBlackCrows(Candle c1, Candle c2, Candle c3)
    {
        if (!IsBearish(c1) || !IsBearish(c2) || !IsBearish(c3)) return false;
        if (!(c2.Close < c1.Close && c3.Close < c2.Close)) return false;

        var m1 = SingleCandleMetrics(c1);
        var m2 = SingleCandleMetrics(c2);
        var m3 = SingleCandleMetrics(c3);

        var allStrongBodies = m1.BodyPercent > _thresholds.ThreeSoldiersCrowsMinBodyPercent
            && m2.BodyPercent > _thresholds.ThreeSoldiersCrowsMinBodyPercent
            && m3.BodyPercent > _thresholds.ThreeSoldiersCrowsMinBodyPercent;

        var noLongLowerWicks = m1.LowerWickPercent < _thresholds.PinBarMinWickPercent
            && m2.LowerWickPercent < _thresholds.PinBarMinWickPercent
            && m3.LowerWickPercent < _thresholds.PinBarMinWickPercent;

        return allStrongBodies && noLongLowerWicks;
    }
}

// IMPLEMENTATION NOTE on two judgment calls §10's prose leaves to the implementer:
//
// 1. "Long bearish/bullish candle" (Morning/Evening Star, first candle): §10 doesn't
//    give this an explicit numeric threshold the way it does for Marubozu (>90%) or
//    Doji (<10%). This implementation uses (MarubozuMinBodyPercent - 30) = 60% as the
//    "long candle" cutoff — i.e. meaningfully more decisive than a 50/50 body but not
//    as extreme as a full Marubozu. Revisit during Phase 6 (§2's "revisit every default"
//    validation pass) against real chart examples if this reads too strict/loose in practice.
//
// 2. "Small body/doji" (Morning/Evening Star, middle candle): implemented as
//    Body% < 3x DojiMaxBodyPercent (30% at defaults) — wider than a strict Doji
//    (10%) since classic star-pattern literature accepts a "small real body," not
//    only a true doji, for the middle candle. Same Phase-6 revisit note applies.
//
// Both are configurable via CandlePsychologyThresholds derivation, not new hardcoded
// magic numbers — but flagged here explicitly since §10 left them as prose rather
// than a table row, unlike every other pattern in this file.
