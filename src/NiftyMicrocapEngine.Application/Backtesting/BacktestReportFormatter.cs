using System.Globalization;
using System.Text;

namespace NiftyMicrocapEngine.Application.Backtesting;

/// <summary>
/// The §24/§25 "walk-forward backtest harness + sample report" and
/// "weight/threshold tuning" deliverables need an actual artifact, not just
/// console output — this produces both a human-readable Markdown summary
/// (bucket-level stats, matching what a reader would use to judge whether
/// StrongBuy actually outperforms Buy, which is the whole point of having
/// two tiers) and a trade-level CSV (one row per signal, for anyone who
/// wants to pull it into a spreadsheet and try alternative weight/threshold
/// assumptions against the same trade set — this IS the "weight/threshold
/// tuning" support: the report gives the raw material, not an automated
/// optimizer, since re-deriving DecisionEngineOptions' weights from a
/// backtest is a judgment call about overfitting risk that belongs with a
/// human reviewing the CSV, not a black-box optimizer picking new defaults.
/// </summary>
public static class BacktestReportFormatter
{
    public static string ToMarkdown(BacktestReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Walk-forward backtest report — {report.RunAt:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine($"- Window: {report.Request.StartDate} .. {report.Request.EndDate}");
        sb.AppendLine($"- Cadence: every {report.Request.CadenceTradingDays} trading day(s)");
        sb.AppendLine($"- Symbols walked: {report.SymbolsWalked}");
        sb.AppendLine($"- As-of dates evaluated: {report.TotalAsOfDatesEvaluated}");
        sb.AppendLine($"- Buy/StrongBuy signals generated: {report.TotalSignalsGenerated}");
        sb.AppendLine($"- Run duration: {report.Duration}");
        sb.AppendLine();
        sb.AppendLine("## Bucket stats");
        sb.AppendLine();
        sb.AppendLine("| Decision | Signals | Simulated | Wins | Losses | Timed out | Win rate | Avg R | Expected R/signal |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var b in report.BucketStats)
        {
            // Explicit InvariantCulture here (P1/F2 both involve a decimal
            // separator that varies by culture — e.g. "0.83" vs "0,83") —
            // this line was the one spot in this file NOT already forcing
            // it, found while hand-verifying a sample report's exact output
            // against this code; ToCsv already did this correctly
            // throughout, this brings ToMarkdown in line with it.
            var winRate = b.WinRate.ToString("P1", CultureInfo.InvariantCulture);
            var avgR = b.AverageRMultiple.ToString("F2", CultureInfo.InvariantCulture);
            var expectedR = b.ExpectedRPerSignal.ToString("F2", CultureInfo.InvariantCulture);
            sb.AppendLine($"| {b.Decision} | {b.SignalCount} | {b.TradesSimulated} | {b.Wins} | {b.Losses} | {b.TimedOut} | {winRate} | {avgR} | {expectedR} |");
        }
        sb.AppendLine();
        sb.AppendLine("Expected R/signal = WinRate × avg-winning-R − (1−WinRate) × |avg-losing-R|. StrongBuy's expected R should exceed Buy's for the two-tier confidence scoring (§14) to be adding value rather than just noise — if it doesn't on a given run, that's a signal to revisit DecisionEngineOptions' layer weights or the StrongBuy confidence threshold, not to distrust this report.");
        sb.AppendLine();
        sb.AppendLine("NOTE: as computed here, Expected R/signal is algebraically identical to Avg R for the same bucket — both reduce to (sum of realized R-multiples) / (count), since every simulated trade is counted in exactly one of the win or loss terms. This formula becomes genuinely informative once win-rate and average-win/average-loss are estimated INDEPENDENTLY (e.g. testing a hypothetical shift in win rate against the realized payoff ratio) rather than recomputed from the same realized sample — comparing the two columns above won't show anything the Avg R column doesn't already.");
        sb.AppendLine();
        sb.AppendLine($"Trade-level detail (one row per signal) is in the accompanying CSV.");
        return sb.ToString();
    }

    public static string ToCsv(BacktestReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SymbolId,NseSymbol,AsOfDate,Decision,ConfidenceScore,Direction,Entry,StopLoss,Target1,Target2,Target3,RiskRewardRatio,ResultKind,ExitPrice,ExitDate,HoldingTradingDays,RMultiple");
        foreach (var t in report.Trades)
        {
            var direction = t.Plan.Entry >= t.Plan.StopLoss ? "Bullish" : "Bearish"; // TradePlan itself doesn't carry direction; inferred the same way BuildTradePlanFor's caller already knows it (stop below entry = long)
            sb.AppendLine(string.Join(",",
                t.SymbolId,
                t.NseSymbol,
                t.AsOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                t.Decision,
                t.ConfidenceScore.ToString("F2", CultureInfo.InvariantCulture),
                direction,
                t.Plan.Entry.ToString("F2", CultureInfo.InvariantCulture),
                t.Plan.StopLoss.ToString("F2", CultureInfo.InvariantCulture),
                t.Plan.Target1.ToString("F2", CultureInfo.InvariantCulture),
                t.Plan.Target2.ToString("F2", CultureInfo.InvariantCulture),
                t.Plan.Target3.ToString("F2", CultureInfo.InvariantCulture),
                t.Plan.RiskRewardRatio.ToString("F2", CultureInfo.InvariantCulture),
                t.ResultKind,
                t.ExitPrice.ToString("F2", CultureInfo.InvariantCulture),
                t.ExitDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                t.HoldingTradingDays,
                t.RMultiple.ToString("F2", CultureInfo.InvariantCulture)));
        }
        return sb.ToString();
    }
}
