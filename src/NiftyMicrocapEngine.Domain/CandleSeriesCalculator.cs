namespace NiftyMicrocapEngine.Domain;

/// <summary>
/// Derived, per-candle values computed from an ordered candle sequence — several
/// (True Range, Gap, Log Return) need the prior candle, which is why these live
/// here rather than as properties on Candle itself (§5.1).
/// </summary>
public sealed record CandleDerivedValues(
    decimal TypicalPrice,
    decimal MedianPrice,
    decimal TrueRange,
    decimal? Atr,
    decimal? LogReturn,
    decimal Gap,
    decimal BodySize,
    decimal UpperWick,
    decimal LowerWick,
    decimal BodyPercent,
    decimal RangePercent);

/// <summary>
/// Consumes an ascending, gap-tolerant ordered sequence of closed Candles for one
/// symbol/timeframe and computes CandleDerivedValues for each. ATR uses Wilder's
/// smoothing (the standard ATR(14) definition) once at least <paramref name="atrPeriod"/>
/// true-range values are available; before that, Atr is null rather than a partial
/// or fabricated average — see the no-repaint / no-fabrication conventions used
/// throughout this codebase (build spec §21, and the audited trade-duration fix).
/// </summary>
public sealed class CandleSeriesCalculator
{
    private readonly int _atrPeriod;

    public CandleSeriesCalculator(int atrPeriod = 14)
    {
        if (atrPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(atrPeriod));
        _atrPeriod = atrPeriod;
    }

    public IReadOnlyList<CandleDerivedValues> Compute(IReadOnlyList<Candle> orderedCandles)
    {
        var results = new List<CandleDerivedValues>(orderedCandles.Count);
        decimal? previousClose = null;
        decimal? previousAtr = null;
        var trueRanges = new List<decimal>(_atrPeriod);

        for (var i = 0; i < orderedCandles.Count; i++)
        {
            var candle = orderedCandles[i];
            var range = candle.High - candle.Low;

            var typicalPrice = (candle.High + candle.Low + candle.Close) / 3m;
            var medianPrice = (candle.High + candle.Low) / 2m;

            var trueRange = previousClose is null
                ? range
                : Math.Max(range, Math.Max(Math.Abs(candle.High - previousClose.Value), Math.Abs(candle.Low - previousClose.Value)));

            decimal? atr;
            if (previousAtr is null)
            {
                trueRanges.Add(trueRange);
                if (trueRanges.Count >= _atrPeriod)
                {
                    atr = trueRanges.Average();
                }
                else
                {
                    atr = null;
                }
            }
            else
            {
                // Wilder's smoothing: ATR_t = (ATR_(t-1) * (n-1) + TR_t) / n
                atr = (previousAtr.Value * (_atrPeriod - 1) + trueRange) / _atrPeriod;
            }

            var logReturn = previousClose is null || previousClose.Value <= 0
                ? (decimal?)null
                : (decimal)Math.Log((double)(candle.Close / previousClose.Value));

            var gap = previousClose is null ? 0m : candle.Open - previousClose.Value;

            var bodySize = Math.Abs(candle.Close - candle.Open);
            var upperWick = candle.High - Math.Max(candle.Open, candle.Close);
            var lowerWick = Math.Min(candle.Open, candle.Close) - candle.Low;
            var bodyPercent = range == 0 ? 0m : bodySize / range * 100m;
            var rangePercent = candle.Open == 0 ? 0m : range / candle.Open * 100m;

            results.Add(new CandleDerivedValues(
                TypicalPrice: typicalPrice,
                MedianPrice: medianPrice,
                TrueRange: trueRange,
                Atr: atr,
                LogReturn: logReturn,
                Gap: gap,
                BodySize: bodySize,
                UpperWick: upperWick,
                LowerWick: lowerWick,
                BodyPercent: bodyPercent,
                RangePercent: rangePercent));

            previousClose = candle.Close;
            previousAtr = atr;
        }

        return results;
    }
}
