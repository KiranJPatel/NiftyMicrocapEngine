using NiftyMicrocapEngine.Application.Indicators.Momentum;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Indicators;

/// <summary>
/// Golden-file test — cross-checked against an authoritative EXTERNAL
/// reference, not just internal formula self-consistency (which the
/// existing RsiIndicator tests already cover). Two independent sources were
/// checked and agree on the formula:
///
/// - StockCharts.com's ChartSchool ("Relative Strength Index (RSI)"):
///   "First Average Gain = Sum of Gains over the past 14 periods / 14 ...
///   Average Gain = [(previous Average Gain) x 13 + current Gain] / 14" —
///   the exact Wilder smoothing this codebase's RsiIndicator implements
///   (period-1 in place of StockCharts' literal "13" for the default
///   period=14 case).
/// - docs.supra.com's worked numeric example (period=5, for a smaller,
///   fully-traceable table): states "Total Gains = 2 + 0 + 2 + 1 + 2 = 7,
///   Total Losses = 0 + 1 + 0 + 0 + 0 = 1" giving avgGain=1.4/avgLoss=0.2
///   and RSI=87.5 after the 6th close, then "Period 7 shows a loss of 1
///   (price went from 26 to 25)" giving avgGain=1.12/avgLoss=0.36 and
///   RSI≈75.67 after the 7th close.
///
/// The source doesn't publish the raw price series directly, only the
/// per-period gain/loss figures plus the one explicit price point ("26 to
/// 25"). The 7-point close series below (20, 22, 21, 23, 24, 26, 25) was
/// reconstructed by working backward from that anchor through the stated
/// gain/loss sequence [+2, -1, +2, +1, +2, -1] — each step checked against
/// the source's stated Total Gains/Losses before use here.
///
/// The final assertion (75.68, not the source's rounded 75.67) is
/// deliberate: RS = 1.12/0.36 = 28/9 is a repeating decimal, and the source
/// rounds RS to 3.11 BEFORE computing RSI from it, propagating a small
/// rounding error (100 - 100/4.11 = 75.67). Carrying full decimal precision
/// through both steps — no intermediate display-rounding — gives
/// 75.675675..., which correctly rounds to 75.68. This is not a
/// disagreement with the source about the formula, only about whether to
/// round mid-calculation; RsiIndicator (like any correct implementation)
/// does not round until the final displayed value.
/// </summary>
public class RsiGoldenFileTests
{
    private static Candle Candle(int day, decimal close) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: close, High: close + 1, Low: close - 1, Close: close, AdjClose: close, Volume: 1000);

    private static ProcessingContext Ctx() => new();

    private static readonly decimal[] ReconstructedCloses = { 20m, 22m, 21m, 23m, 24m, 26m, 25m };

    [Fact]
    public async Task Rsi_Period5_MatchesPublishedWorkedExample_AtFirstAvailableValue()
    {
        var rsi = new RsiIndicator(period: 5);

        // Only the first 6 closes — the source states the first RSI value
        // becomes available "after Period 6 closes" (5 changes for a
        // period=5 RSI).
        for (var i = 0; i < 6; i++)
        {
            await rsi.OnBarClosedAsync(Candle(i + 1, ReconstructedCloses[i]), Ctx(), default);
        }

        Assert.Equal(IndicatorHealth.OK, rsi.Health);
        Assert.Equal(87.5m, rsi.CurrentValue);
    }

    [Fact]
    public async Task Rsi_Period5_MatchesPublishedWorkedExample_AtSecondValue()
    {
        var rsi = new RsiIndicator(period: 5);

        for (var i = 0; i < ReconstructedCloses.Length; i++)
        {
            await rsi.OnBarClosedAsync(Candle(i + 1, ReconstructedCloses[i]), Ctx(), default);
        }

        Assert.Equal(IndicatorHealth.OK, rsi.Health);
        Assert.Equal(75.68m, Math.Round(rsi.CurrentValue!.Value, 2)); // see class doc comment on the 75.67 vs 75.68 discrepancy
    }
}
