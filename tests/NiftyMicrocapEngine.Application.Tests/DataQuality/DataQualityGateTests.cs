using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataQuality;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.DataQuality;

public class DataQualityGateTests
{
    private static DataQualityGate BuildGate(DataQualityGateOptions? options = null) =>
        new(Options.Create(options ?? new DataQualityGateOptions { TrailingWindowDays = 60, MinimumNonZeroVolumeDays = 30, MaxConsecutiveNoTradeDays = 10 }));

    private static Candle Candle(int day, long volume) => new(
        1, Timeframe.Daily, new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        100m, 105m, 99m, 102m, 102m, volume);

    private static List<DateOnly> ExpectedDaysFrom(IReadOnlyList<Candle> candles) =>
        candles.Select(c => DateOnly.FromDateTime(c.Timestamp.UtcDateTime)).ToList();

    [Fact]
    public void Evaluate_HealthySymbol_Passes()
    {
        var gate = BuildGate();
        var candles = Enumerable.Range(1, 60).Select(d => Candle(d, 1000)).ToList();

        var result = gate.Evaluate(candles, ExpectedDaysFrom(candles));

        Assert.True(result.Passed);
        Assert.Empty(result.FailureReasons);
    }

    [Fact]
    public void Evaluate_TooFewNonZeroVolumeDays_Fails()
    {
        var options = new DataQualityGateOptions { TrailingWindowDays = 60, MinimumNonZeroVolumeDays = 30, MaxConsecutiveNoTradeDays = 60 };
        var gate = BuildGate(options);

        var candles = Enumerable.Range(1, 60)
            .Select(d => Candle(d, d % 3 == 0 ? 1000 : 0))
            .ToList();

        var result = gate.Evaluate(candles, ExpectedDaysFrom(candles));

        Assert.False(result.Passed);
        Assert.Contains(result.FailureReasons, r => r.Contains("non-zero-volume"));
    }

    [Fact]
    public void Evaluate_TooManyConsecutiveNoTradeDays_Fails()
    {
        var options = new DataQualityGateOptions { TrailingWindowDays = 60, MinimumNonZeroVolumeDays = 0, MaxConsecutiveNoTradeDays = 10 };
        var gate = BuildGate(options);

        var candles = new List<Candle>();
        for (var d = 1; d <= 60; d++)
        {
            var volume = d is >= 20 and <= 32 ? 0 : 1000;
            candles.Add(Candle(d, volume));
        }

        var result = gate.Evaluate(candles, ExpectedDaysFrom(candles));

        Assert.False(result.Passed);
        Assert.Contains(result.FailureReasons, r => r.Contains("consecutive no-trade"));
    }

    [Fact]
    public void Evaluate_ConsecutiveNoTradeAtExactlyTheLimit_Passes()
    {
        var options = new DataQualityGateOptions { TrailingWindowDays = 60, MinimumNonZeroVolumeDays = 0, MaxConsecutiveNoTradeDays = 10 };
        var gate = BuildGate(options);

        var candles = new List<Candle>();
        for (var d = 1; d <= 60; d++)
        {
            var volume = d is >= 20 and <= 29 ? 0 : 1000;
            candles.Add(Candle(d, volume));
        }

        var result = gate.Evaluate(candles, ExpectedDaysFrom(candles));

        Assert.True(result.Passed);
    }

    [Fact]
    public void Evaluate_CalendarGap_Fails()
    {
        var gate = BuildGate();
        var candles = Enumerable.Range(1, 60).Where(d => d != 30).Select(d => Candle(d, 1000)).ToList();

        var expectedDays = Enumerable.Range(1, 60).Select(d => new DateOnly(2026, 1, d)).ToList();

        var result = gate.Evaluate(candles, expectedDays);

        Assert.False(result.Passed);
        Assert.Contains(result.FailureReasons, r => r.Contains("calendar gap"));
    }

    [Fact]
    public void Evaluate_ExpectedDaysOutsideCandleRange_NotCountedAsGaps()
    {
        var gate = BuildGate();
        var candles = Enumerable.Range(1, 60).Select(d => Candle(d, 1000)).ToList();

        var expectedDays = Enumerable.Range(1, 60).Select(d => new DateOnly(2026, 1, d))
            .Concat(Enumerable.Range(61, 10).Select(d => new DateOnly(2026, 3, d - 59)))
            .ToList();

        var result = gate.Evaluate(candles, expectedDays);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Evaluate_EmptyCandleList_Fails()
    {
        var gate = BuildGate();
        var result = gate.Evaluate(Array.Empty<Candle>(), Array.Empty<DateOnly>());

        Assert.False(result.Passed);
    }

    [Fact]
    public void Evaluate_OnlyUsesTrailingWindowNotFullHistory()
    {
        var options = new DataQualityGateOptions { TrailingWindowDays = 60, MinimumNonZeroVolumeDays = 30, MaxConsecutiveNoTradeDays = 10 };
        var gate = BuildGate(options);

        var candles = new List<Candle>();
        for (var d = 1; d <= 100; d++)
        {
            var volume = d <= 40 ? 0 : 1000;
            candles.Add(Candle(d, volume));
        }

        var result = gate.Evaluate(candles, ExpectedDaysFrom(candles.TakeLast(60).ToList()));

        Assert.True(result.Passed);
    }
}
