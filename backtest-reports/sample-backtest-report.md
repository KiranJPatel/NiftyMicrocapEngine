<!--
SAMPLE ARTIFACT — NOT FROM A REAL BACKTEST RUN.

This sandbox has no network access to Yahoo/NSE and no .NET SDK, so there
is no real market data or compiler available to actually run
WalkForwardBacktester against. The trade data below (symbols, dates,
prices, outcomes) is invented for illustration.

What IS real: this file's structure, headers, and every number's FORMAT
were hand-traced line-by-line against BacktestReportFormatter.ToMarkdown's
actual source (see src/NiftyMicrocapEngine.Application/Backtesting/), not
guessed at — including the WinRate/AvgR/ExpectedR arithmetic, computed by
hand from the invented trades using the exact formula in
WalkForwardBacktester.BuildBucketStats. If you feed the real
WalkForwardBacktester the six BacktestTradeOutcome records shown in the
accompanying CSV, it produces this exact Markdown output (module the
RunAt/Duration/AsOfDatesEvaluated/SymbolsWalked header fields, which are
run-specific and invented here for illustration too).

One genuine finding surfaced while doing this hand-trace, now fixed in the
real code and reflected below: ToMarkdown didn't force InvariantCulture on
its WinRate/AvgR/ExpectedR formatting (ToCsv already did) — meaning the
Markdown report's number formatting depended on whatever culture the
hosting machine defaulted to. Fixed to match ToCsv's convention.
-->

# Walk-forward backtest report — 2026-06-01

- Window: 2025-06-01 .. 2026-06-01
- Cadence: every 10 trading day(s)
- Symbols walked: 30
- As-of dates evaluated: 742
- Buy/StrongBuy signals generated: 6
- Run duration: 00:14:32.1170000

## Bucket stats

| Decision | Signals | Simulated | Wins | Losses | Timed out | Win rate | Avg R | Expected R/signal |
|---|---|---|---|---|---|---|---|---|
| StrongBuy | 3 | 3 | 2 | 1 | 1 | 66.7% | 0.83 | 0.83 |
| Buy | 3 | 3 | 1 | 2 | 0 | 33.3% | -0.33 | -0.33 |

Expected R/signal = WinRate × avg-winning-R − (1−WinRate) × |avg-losing-R|. StrongBuy's expected R should exceed Buy's for the two-tier confidence scoring (§14) to be adding value rather than just noise — if it doesn't on a given run, that's a signal to revisit DecisionEngineOptions' layer weights or the StrongBuy confidence threshold, not to distrust this report.

NOTE: as computed here, Expected R/signal is algebraically identical to Avg R for the same bucket — both reduce to (sum of realized R-multiples) / (count), since every simulated trade is counted in exactly one of the win or loss terms. This formula becomes genuinely informative once win-rate and average-win/average-loss are estimated INDEPENDENTLY (e.g. testing a hypothetical shift in win rate against the realized payoff ratio) rather than recomputed from the same realized sample — comparing the two columns above won't show anything the Avg R column doesn't already.

Trade-level detail (one row per signal) is in the accompanying CSV.
