using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Backtesting;

/// <summary>
/// Pure, DB-free forward walk: given a TradePlan and the Daily candles that
/// followed the signal, determines which price level was reached first.
/// Deliberately conservative on same-bar ambiguity: if a single bar's
/// High/Low range spans BOTH the stop and a target, this treats it as a stop
/// hit — a real intraday path within that bar is unknown from Daily OHLCV
/// alone, and assuming the better outcome would optimistically bias every
/// ambiguous bar in the backtest's favor. This is a judgment call the spec
/// doesn't dictate; a stricter alternative (discard the bar as ambiguous)
/// was rejected because it would silently shrink the sample in exactly the
/// volatile conditions most worth measuring.
/// </summary>
public static class BacktestOutcomeSimulator
{
    public static BacktestTradeOutcome Simulate(
        int symbolId,
        string nseSymbol,
        DateOnly asOfDate,
        DecisionEngineResult decision,
        TradePlan plan,
        TrendDirection direction,
        IReadOnlyList<Candle> forwardCandlesOrderedByTime,
        int maxHoldingTradingDaysFallback)
    {
        var maxHoldingDays = plan.EstimatedDuration.HasValue
            ? Math.Max(1, (int)Math.Ceiling(plan.EstimatedDuration.Value.TotalDays * 5.0 / 7.0)) // calendar days -> approx trading days
            : maxHoldingTradingDaysFallback;

        var window = forwardCandlesOrderedByTime.Take(maxHoldingDays).ToList();
        if (window.Count == 0)
        {
            return new BacktestTradeOutcome(symbolId, nseSymbol, asOfDate, decision.Outcome, decision.ConfidenceScore,
                plan, BacktestOutcomeKind.InsufficientForwardData, plan.Entry, null, 0, 0m);
        }

        var isLong = direction == TrendDirection.Bullish;
        var riskPerShare = Math.Abs(plan.Entry - plan.StopLoss);
        if (riskPerShare == 0m)
        {
            // A zero-width stop makes R-multiple undefined; treat as
            // insufficient rather than dividing by zero or fabricating a
            // multiple with no denominator basis.
            return new BacktestTradeOutcome(symbolId, nseSymbol, asOfDate, decision.Outcome, decision.ConfidenceScore,
                plan, BacktestOutcomeKind.InsufficientForwardData, plan.Entry, null, 0, 0m);
        }

        for (var i = 0; i < window.Count; i++)
        {
            var bar = window[i];
            var stopHit = isLong ? bar.Low <= plan.StopLoss : bar.High >= plan.StopLoss;
            var t3Hit = isLong ? bar.High >= plan.Target3 : bar.Low <= plan.Target3;
            var t2Hit = isLong ? bar.High >= plan.Target2 : bar.Low <= plan.Target2;
            var t1Hit = isLong ? bar.High >= plan.Target1 : bar.Low <= plan.Target1;

            if (stopHit)
            {
                return BuildOutcome(BacktestOutcomeKind.HitStop, plan.StopLoss, bar, i + 1);
            }
            if (t3Hit)
            {
                return BuildOutcome(BacktestOutcomeKind.HitTarget3, plan.Target3, bar, i + 1);
            }
            if (t2Hit)
            {
                return BuildOutcome(BacktestOutcomeKind.HitTarget2, plan.Target2, bar, i + 1);
            }
            if (t1Hit)
            {
                return BuildOutcome(BacktestOutcomeKind.HitTarget1, plan.Target1, bar, i + 1);
            }
        }

        // Timed out: exit at the last available bar's close, still scored
        // against the risk basis so a timeout that drifted favorably or
        // unfavorably shows up in the R-multiple distribution rather than
        // being excluded.
        var lastBar = window[^1];
        return BuildOutcome(BacktestOutcomeKind.TimedOut, lastBar.Close, lastBar, window.Count);

        BacktestTradeOutcome BuildOutcome(BacktestOutcomeKind kind, decimal exitPrice, Candle exitBar, int holdingDays)
        {
            var rawMove = isLong ? exitPrice - plan.Entry : plan.Entry - exitPrice;
            var rMultiple = rawMove / riskPerShare;
            return new BacktestTradeOutcome(symbolId, nseSymbol, asOfDate, decision.Outcome, decision.ConfidenceScore,
                plan, kind, exitPrice, DateOnly.FromDateTime(exitBar.Timestamp.UtcDateTime), holdingDays, rMultiple);
        }
    }
}
