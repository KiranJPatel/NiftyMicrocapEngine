using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.DataQuality;

public enum CircuitBandState { None, UpperLocked, LowerLocked }

/// <summary>
/// Implements build spec section 6.8: a Buy on a stock locked at its upper
/// circuit isn't actionable (no seller to fill against), and a short at the
/// lower circuit is equally unreachable. Feeds the Decision Engine's
/// CircuitLocked hard gate directly.
///
/// Two detection paths, in order of precedence:
/// 1. Zero-intraday-range heuristic (the original, feed-independent check):
///    a daily candle where High equals Low equals Close is the unambiguous
///    signature of a full-day circuit lock, since a genuinely range-bound
///    but freely-traded day essentially never closes with zero range.
/// 2. Band-aware check (added once a real feed — INseCircuitBandProvider —
///    was verified reachable): when the caller supplies the symbol's actual
///    published circuit-band percentage, a close that moved that far from
///    the previous close is ALSO flagged as locked, even without zero
///    intraday range. This catches the case the original heuristic's doc
///    comment flagged as under-detected: a circuit hit mid-session that
///    still closed off the limit rather than staying pinned there all day.
///
/// Pass null for circuitBandFraction (or use the 2-argument overload) when
/// the real band for a symbol is unknown or the feed is unavailable — this
/// falls back to the zero-range-only check exactly as before, so every
/// existing caller keeps working unchanged.
/// </summary>
public interface ICircuitBandTracker
{
    CircuitBandState DetectFromLatestCandle(Candle latestDailyCandle, Candle? previousDailyCandle);

    CircuitBandState DetectFromLatestCandle(Candle latestDailyCandle, Candle? previousDailyCandle, decimal? circuitBandFraction);
}

public sealed class CircuitBandTracker : ICircuitBandTracker
{
    /// <summary>
    /// NSE's published band is a rounded figure; a stock that "effectively"
    /// hit its limit can show a slightly smaller move once compounded
    /// through tick-size rounding. 0.2 percentage points of slack avoids
    /// missing a real lock over that rounding without being so loose it
    /// flags ordinary volatile trading as a lock.
    /// </summary>
    private const decimal BandTolerance = 0.002m;

    public CircuitBandState DetectFromLatestCandle(Candle latestDailyCandle, Candle? previousDailyCandle) =>
        DetectFromLatestCandle(latestDailyCandle, previousDailyCandle, circuitBandFraction: null);

    public CircuitBandState DetectFromLatestCandle(Candle latestDailyCandle, Candle? previousDailyCandle, decimal? circuitBandFraction)
    {
        if (previousDailyCandle is null)
        {
            // Locked or moved, but no prior close to determine direction (or
            // compute a percentage move at all). Guessing the direction of a
            // hard gate has real consequences, so report None here rather
            // than an unjustified guess — the caller's data-quality layer
            // should separately flag the missing prior-close.
            return CircuitBandState.None;
        }

        var hasZeroIntradayRange = latestDailyCandle.High == latestDailyCandle.Low && latestDailyCandle.Low == latestDailyCandle.Close;
        if (hasZeroIntradayRange)
        {
            return latestDailyCandle.Close > previousDailyCandle.Close ? CircuitBandState.UpperLocked : CircuitBandState.LowerLocked;
        }

        if (circuitBandFraction is { } band && band > 0 && previousDailyCandle.Close > 0)
        {
            var move = (latestDailyCandle.Close - previousDailyCandle.Close) / previousDailyCandle.Close;
            if (move >= band - BandTolerance) return CircuitBandState.UpperLocked;
            if (move <= -(band - BandTolerance)) return CircuitBandState.LowerLocked;
        }

        return CircuitBandState.None;
    }
}
