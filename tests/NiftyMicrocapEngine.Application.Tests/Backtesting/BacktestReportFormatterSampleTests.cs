using System.IO;
using NiftyMicrocapEngine.Application.Backtesting;
using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.Risk;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Backtesting;

/// <summary>
/// The strongest verification available in this sandbox (no compiler) for
/// the §25 "walk-forward backtest harness + sample report" deliverable:
/// constructs the exact same six trades used in
/// /backtest-reports/sample-backtest-report.md and .csv, and asserts
/// BacktestReportFormatter's real ToMarkdown/ToCsv methods produce output
/// character-for-character identical to those checked-in sample files
/// (module the sample .md's leading HTML disclaimer comment, which isn't
/// part of the formatter's own output). If this test would fail on a real
/// run, the checked-in sample files are wrong and need regenerating from
/// this test's actual output, not the other way around.
/// </summary>
public class BacktestReportFormatterSampleTests
{
    private static TradePlan Plan(decimal entry, decimal stop, decimal t1, decimal t2, decimal t3) =>
        new(entry, stop, t1, t2, t3, RiskPercent: 0.01m, RiskRewardRatio: 2.50m, InvalidationLevel: "sample", EstimatedDuration: TimeSpan.FromDays(10), DurationDataQualityFlag: null);

    private static BacktestTradeOutcome Trade(
        int symbolId, string nseSymbol, DateOnly asOfDate, DecisionOutcome decision, decimal confidence,
        TradePlan plan, BacktestOutcomeKind resultKind, decimal exitPrice, DateOnly exitDate, int holdingDays, decimal rMultiple) =>
        new(symbolId, nseSymbol, asOfDate, decision, confidence, plan, resultKind, exitPrice, exitDate, holdingDays, rMultiple);

    private static BacktestReport BuildSampleReport()
    {
        var trades = new List<BacktestTradeOutcome>
        {
            Trade(1, "ALPHACO", new DateOnly(2026, 1, 15), DecisionOutcome.StrongBuy, 85.00m,
                Plan(150.00m, 142.50m, 157.50m, 165.00m, 172.50m),
                BacktestOutcomeKind.HitTarget2, 165.00m, new DateOnly(2026, 1, 29), 10, 2.00m),
            Trade(2, "BETAIND", new DateOnly(2026, 2, 10), DecisionOutcome.StrongBuy, 82.00m,
                Plan(88.00m, 84.00m, 92.00m, 96.00m, 100.00m),
                BacktestOutcomeKind.HitStop, 84.00m, new DateOnly(2026, 2, 13), 3, -1.00m),
            Trade(3, "GAMMAFIN", new DateOnly(2026, 3, 5), DecisionOutcome.StrongBuy, 88.00m,
                Plan(210.00m, 199.50m, 220.50m, 231.00m, 241.50m),
                BacktestOutcomeKind.TimedOut, 225.75m, new DateOnly(2026, 4, 5), 22, 1.50m),
            Trade(4, "DELTAENG", new DateOnly(2026, 1, 22), DecisionOutcome.Buy, 70.00m,
                Plan(45.00m, 42.75m, 47.25m, 49.50m, 51.75m),
                BacktestOutcomeKind.HitTarget1, 47.25m, new DateOnly(2026, 1, 27), 5, 1.00m),
            Trade(5, "EPSILONPH", new DateOnly(2026, 2, 18), DecisionOutcome.Buy, 68.00m,
                Plan(320.00m, 304.00m, 336.00m, 352.00m, 368.00m),
                BacktestOutcomeKind.HitStop, 304.00m, new DateOnly(2026, 2, 19), 1, -1.00m),
            Trade(6, "ZETACHEM", new DateOnly(2026, 3, 12), DecisionOutcome.Buy, 72.00m,
                Plan(95.00m, 90.25m, 99.75m, 104.50m, 109.25m),
                BacktestOutcomeKind.HitStop, 90.25m, new DateOnly(2026, 3, 14), 2, -1.00m),
        };

        var bucketStats = new List<BacktestBucketStats>
        {
            // Hand-computed exactly as WalkForwardBacktester.BuildBucketStats
            // would from the three StrongBuy trades above: wins=2 (2.00,
            // 1.50 R), losses=1 (-1.00 R, HitStop), timedOut=1;
            // winRate=2/3, avgR=(2.00-1.00+1.50)/3=0.8333...
            new(DecisionOutcome.StrongBuy, SignalCount: 3, TradesSimulated: 3, Wins: 2, Losses: 1, TimedOut: 1,
                WinRate: 2m / 3m, AverageRMultiple: 2.5m / 3m, ExpectedRPerSignal: 2.5m / 3m),
            // Buy trades: wins=1 (1.00 R), losses=2 (-1.00, -1.00 R);
            // winRate=1/3, avgR=(1.00-1.00-1.00)/3=-0.3333...
            new(DecisionOutcome.Buy, SignalCount: 3, TradesSimulated: 3, Wins: 1, Losses: 2, TimedOut: 0,
                WinRate: 1m / 3m, AverageRMultiple: -1m / 3m, ExpectedRPerSignal: -1m / 3m),
        };

        var request = new BacktestRequest(new DateOnly(2025, 6, 1), new DateOnly(2026, 6, 1), CadenceTradingDays: 10);
        return new BacktestReport(
            Request: request, RunAt: new DateOnly(2026, 6, 1), SymbolsWalked: 30, TotalAsOfDatesEvaluated: 742,
            TotalSignalsGenerated: 6, BucketStats: bucketStats, Trades: trades, Duration: new TimeSpan(0, 0, 14, 32, 117));
    }

    [Fact]
    public void ToMarkdown_MatchesTheCheckedInSampleReport()
    {
        var report = BuildSampleReport();
        var markdown = BacktestReportFormatter.ToMarkdown(report);

        // The checked-in sample file has a leading HTML disclaimer comment
        // (not part of the formatter's actual output) before the real
        // report body begins at the "# Walk-forward..." heading — strip
        // that off before comparing, so this test verifies the ACTUAL
        // generated content, not the documentation wrapper around it.
        var samplePath = FindSampleFile("sample-backtest-report.md");
        var fullSample = File.ReadAllText(samplePath);
        var bodyStart = fullSample.IndexOf("# Walk-forward", StringComparison.Ordinal);
        var sampleBody = fullSample[bodyStart..];

        Assert.Equal(NormalizeLineEndings(sampleBody), NormalizeLineEndings(markdown));
    }

    [Fact]
    public void ToCsv_MatchesTheCheckedInSampleReport()
    {
        var report = BuildSampleReport();
        var csv = BacktestReportFormatter.ToCsv(report);

        var samplePath = FindSampleFile("sample-backtest-report.csv");
        var sampleCsv = File.ReadAllText(samplePath);

        Assert.Equal(NormalizeLineEndings(sampleCsv), NormalizeLineEndings(csv));
    }

    private static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    /// <summary>Walks up from the test assembly's output directory to find the repo-root /backtest-reports/ folder — avoids hardcoding a path that only works from one specific working directory.</summary>
    private static string FindSampleFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "backtest-reports", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {fileName} by walking up from {AppContext.BaseDirectory} — expected it at <repo-root>/backtest-reports/{fileName}.");
    }
}
