using NiftyMicrocapEngine.Application.DataQuality;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.DataQuality;

public class CircuitBandTrackerTests
{
    private readonly CircuitBandTracker _tracker = new();

    private static Candle Candle(decimal open, decimal high, decimal low, decimal close) => new(
        1, Timeframe.Daily, DateTimeOffset.UtcNow, open, high, low, close, close, 1000);

    [Fact]
    public void DetectFromLatestCandle_ZeroRangeAboveYesterdaysClose_IsUpperLocked()
    {
        var previous = Candle(95m, 96m, 94m, 95m);
        var latest = Candle(100m, 100m, 100m, 100m);

        var result = _tracker.DetectFromLatestCandle(latest, previous);

        Assert.Equal(CircuitBandState.UpperLocked, result);
    }

    [Fact]
    public void DetectFromLatestCandle_ZeroRangeBelowYesterdaysClose_IsLowerLocked()
    {
        var previous = Candle(105m, 106m, 104m, 105m);
        var latest = Candle(95m, 95m, 95m, 95m);

        var result = _tracker.DetectFromLatestCandle(latest, previous);

        Assert.Equal(CircuitBandState.LowerLocked, result);
    }

    [Fact]
    public void DetectFromLatestCandle_NormalTradingRange_IsNone()
    {
        var previous = Candle(95m, 96m, 94m, 95m);
        var latest = Candle(96m, 99m, 95m, 98m);

        var result = _tracker.DetectFromLatestCandle(latest, previous);

        Assert.Equal(CircuitBandState.None, result);
    }

    [Fact]
    public void DetectFromLatestCandle_ZeroRangeButNoPreviousCandle_ReturnsNoneRatherThanGuess()
    {
        var latest = Candle(100m, 100m, 100m, 100m);

        var result = _tracker.DetectFromLatestCandle(latest, previousDailyCandle: null);

        Assert.Equal(CircuitBandState.None, result);
    }

    [Fact]
    public void DetectFromLatestCandle_ZeroRangeAtSamePriceAsPreviousClose_IsLowerLockedNotNone()
    {
        var previous = Candle(100m, 101m, 99m, 100m);
        var latest = Candle(100m, 100m, 100m, 100m);

        var result = _tracker.DetectFromLatestCandle(latest, previous);

        Assert.Equal(CircuitBandState.LowerLocked, result);
    }

    // --- Band-aware overload (§6.8's real feed) ---

    [Fact]
    public void DetectFromLatestCandle_BandAware_MoveMatchesPublishedBand_IsUpperLocked_EvenWithoutZeroRange()
    {
        // Not zero-range (High/Low differ from Close), but the close moved
        // exactly the published 5% band — the case the original
        // zero-range-only heuristic's doc comment flagged as under-detected.
        var previous = Candle(100m, 101m, 99m, 100m);
        var latest = Candle(104m, 105.5m, 103.5m, 105m); // +5% close, non-zero range

        var result = _tracker.DetectFromLatestCandle(latest, previous, circuitBandFraction: 0.05m);

        Assert.Equal(CircuitBandState.UpperLocked, result);
    }

    [Fact]
    public void DetectFromLatestCandle_BandAware_MoveWellBelowBand_IsNone()
    {
        var previous = Candle(100m, 101m, 99m, 100m);
        var latest = Candle(101m, 102m, 100m, 101.5m); // +1.5%, nowhere near a 5% band

        var result = _tracker.DetectFromLatestCandle(latest, previous, circuitBandFraction: 0.05m);

        Assert.Equal(CircuitBandState.None, result);
    }

    [Fact]
    public void DetectFromLatestCandle_BandAware_NullBand_FallsBackToZeroRangeHeuristicOnly()
    {
        // A 4.9% move with no known band should NOT be flagged — without a
        // real band to compare against, only the zero-range signature counts.
        var previous = Candle(100m, 101m, 99m, 100m);
        var latest = Candle(103m, 105m, 102m, 104.9m);

        var result = _tracker.DetectFromLatestCandle(latest, previous, circuitBandFraction: null);

        Assert.Equal(CircuitBandState.None, result);
    }

    [Fact]
    public void DetectFromLatestCandle_BandAware_DownwardMoveMatchesBand_IsLowerLocked()
    {
        var previous = Candle(100m, 101m, 99m, 100m);
        var latest = Candle(96m, 96.5m, 94.5m, 95m); // -5%, non-zero range

        var result = _tracker.DetectFromLatestCandle(latest, previous, circuitBandFraction: 0.05m);

        Assert.Equal(CircuitBandState.LowerLocked, result);
    }

    [Fact]
    public void DetectFromLatestCandle_BandAware_TwoArgOverload_StillMatchesOriginalZeroRangeOnlyBehavior()
    {
        // The pre-existing 2-argument overload must behave identically to
        // calling the 3-argument one with circuitBandFraction: null — this
        // is the backward-compatibility guarantee every existing caller
        // (and the other four tests in this file) relies on.
        var previous = Candle(100m, 101m, 99m, 100m);
        var latest = Candle(103m, 105m, 102m, 104.9m); // same as the null-band test above — should also be None

        var result = _tracker.DetectFromLatestCandle(latest, previous);

        Assert.Equal(CircuitBandState.None, result);
    }
}
