using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.DataQuality;

/// <summary>
/// Implements the three section 6.7 checks. All thresholds come from
/// DataQualityGateOptions — never hardcoded, matching this codebase's
/// convention throughout.
/// </summary>
public sealed class DataQualityGate : IDataQualityGate
{
    private readonly DataQualityGateOptions _options;

    public DataQualityGate(IOptions<DataQualityGateOptions> options)
    {
        _options = options.Value;
    }

    public DataQualityGateResult Evaluate(IReadOnlyList<Candle> trailingDailyCandles, IReadOnlyList<DateOnly> expectedTradingDays)
    {
        var reasons = new List<string>();

        var windowCandles = trailingDailyCandles
            .OrderByDescending(c => c.Timestamp)
            .Take(_options.TrailingWindowDays)
            .OrderBy(c => c.Timestamp)
            .ToList();

        var nonZeroVolumeDays = windowCandles.Count(c => c.Volume > 0);
        if (nonZeroVolumeDays < _options.MinimumNonZeroVolumeDays)
        {
            reasons.Add(
                $"Only {nonZeroVolumeDays} non-zero-volume trading days in the trailing {_options.TrailingWindowDays}-day " +
                $"window (minimum required: {_options.MinimumNonZeroVolumeDays}).");
        }

        var maxConsecutiveNoTrade = 0;
        var currentRun = 0;
        foreach (var candle in windowCandles)
        {
            if (candle.Volume == 0)
            {
                currentRun++;
                maxConsecutiveNoTrade = Math.Max(maxConsecutiveNoTrade, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        if (maxConsecutiveNoTrade > _options.MaxConsecutiveNoTradeDays)
        {
            reasons.Add(
                $"{maxConsecutiveNoTrade} consecutive no-trade days detected in the trailing window " +
                $"(maximum allowed: {_options.MaxConsecutiveNoTradeDays}).");
        }

        // expectedTradingDays is the caller-supplied NSE trading calendar for the
        // same window (holidays already excluded — that lookup is out of scope
        // for this gate; it only compares against whatever calendar it's given).
        var actualTradingDates = windowCandles.Select(c => DateOnly.FromDateTime(c.Timestamp.UtcDateTime)).ToHashSet();

        List<DateOnly> relevantExpectedDays;
        if (windowCandles.Count == 0)
        {
            relevantExpectedDays = new List<DateOnly>();
        }
        else
        {
            var earliest = DateOnly.FromDateTime(windowCandles[0].Timestamp.UtcDateTime);
            var latest = DateOnly.FromDateTime(windowCandles[^1].Timestamp.UtcDateTime);
            relevantExpectedDays = expectedTradingDays.Where(d => d >= earliest && d <= latest).ToList();
        }

        var missingDays = relevantExpectedDays.Where(d => !actualTradingDates.Contains(d)).ToList();
        if (missingDays.Count > 0)
        {
            reasons.Add(
                $"{missingDays.Count} expected trading day(s) within the window have no candle data " +
                $"(calendar gap beyond known holidays): {string.Join(", ", missingDays.Take(5))}" +
                (missingDays.Count > 5 ? ", ..." : "") + ".");
        }

        return new DataQualityGateResult(reasons.Count == 0, reasons);
    }
}
