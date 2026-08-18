using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Backtesting;

/// <summary>
/// Phase 6 (§24 in the build roadmap — "Validation"), the next unstarted
/// phase per the spec's own numbering. Walks forward through history calling
/// the SAME engines the live Scanner calls (Structure pipeline, MTF, Regime
/// Filter, Decision Engine, Trade Plan Builder) at a series of historical
/// as-of dates, using only candles closed on or before each as-of date (no
/// lookahead — same §21 no-repaint discipline the live pipeline follows),
/// then simulates what happened to price afterward to score each signal.
///
/// Deliberately does NOT reuse UniverseScanner's private per-symbol methods.
/// UniverseScanner caches broad-market/regime context ONCE per RunAsync call
/// (correct for a live "today" scan — every symbol shares the same "today"),
/// but a walk-forward backtest needs a DIFFERENT regime snapshot at every
/// simulated as-of date, since regime state genuinely changes across a
/// backtest window. This class calls IBroadMarketContextProvider fresh per
/// as-of date instead. The per-date evaluation logic below (structure
/// pipeline → MTF → regime → decision → trade plan) intentionally mirrors
/// UniverseScanner.Stage1.cs/DecisionInput.cs/TradePlan.cs step for step —
/// if that logic changes, this should be revisited to match.
/// </summary>
public interface IWalkForwardBacktester
{
    Task<BacktestReport> RunAsync(BacktestRequest request, CancellationToken ct = default);
}

public sealed record BacktestRequest(
    DateOnly StartDate,
    DateOnly EndDate,
    /// <summary>Simulated decision dates are every CadenceTradingDays'th available Daily candle, not calendar days — naturally skips weekends/holidays. Default 10 (~2 trading weeks), matching this engine's swing-trade holding horizon (§16.1).</summary>
    int CadenceTradingDays = 10,
    /// <summary>Null/empty = use the current universe snapshot's members, capped by MaxSymbols.</summary>
    IReadOnlyList<int>? SymbolIds = null,
    /// <summary>
    /// Caps how many symbols get walked when SymbolIds isn't explicit. Each
    /// symbol re-runs the full structure/indicator pipeline from scratch at
    /// EVERY simulated as-of date (the same cost the live Stage 1 pays once
    /// per "today" — see UniverseScanner's doc comment on write contention),
    /// so a 250-symbol x 50-date backtest is 12,500 full pipeline passes.
    /// Default keeps a single run tractable; raise explicitly for a full
    /// universe backtest.
    /// </summary>
    int MaxSymbols = 30,
    /// <summary>
    /// Trading-day cap used ONLY when TradePlan.EstimatedDuration is null
    /// (the audited §16.1 fallback case — see TradePlanBuilder's doc comment
    /// on DurationDataQualityFlag). This is a backtest-only simulation
    /// parameter, not a restatement of the Trade Plan Builder's own
    /// duration estimate — kept distinct so a reader never mistakes it for
    /// production duration logic.
    /// </summary>
    int MaxHoldingTradingDaysFallback = 40);

public enum BacktestOutcomeKind { HitTarget1, HitTarget2, HitTarget3, HitStop, TimedOut, InsufficientForwardData }

public sealed record BacktestTradeOutcome(
    int SymbolId,
    string NseSymbol,
    DateOnly AsOfDate,
    DecisionOutcome Decision,
    decimal ConfidenceScore,
    TradePlan Plan,
    BacktestOutcomeKind ResultKind,
    decimal ExitPrice,
    DateOnly? ExitDate,
    int HoldingTradingDays,
    /// <summary>Realized reward:risk multiple — (ExitPrice - Entry) / (Entry - StopLoss), sign-adjusted for direction. Positive = win, negative = loss, by construction (not just by target/stop label) so a partial adverse move that still exits above breakeven reads correctly.</summary>
    decimal RMultiple);

/// <summary>One DecisionOutcome bucket's aggregated stats, e.g. all StrongBuy signals across the run.</summary>
public sealed record BacktestBucketStats(
    DecisionOutcome Decision,
    int SignalCount,
    int TradesSimulated,
    int Wins,
    int Losses,
    int TimedOut,
    decimal WinRate,
    decimal AverageRMultiple,
    /// <summary>WinRate × average-winning-R minus (1-WinRate) × average-losing-R — the expected R per signal in this bucket, section 24's "weight/threshold tuning" needs exactly this to compare buckets.</summary>
    decimal ExpectedRPerSignal);

public sealed record BacktestReport(
    BacktestRequest Request,
    DateOnly RunAt,
    int SymbolsWalked,
    int TotalAsOfDatesEvaluated,
    int TotalSignalsGenerated,
    IReadOnlyList<BacktestBucketStats> BucketStats,
    IReadOnlyList<BacktestTradeOutcome> Trades,
    TimeSpan Duration);
