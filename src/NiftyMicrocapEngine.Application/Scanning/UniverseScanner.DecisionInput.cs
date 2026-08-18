using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Scanning;

public sealed partial class UniverseScanner
{
    /// <summary>
    /// Assembles a DecisionEngineInput from a structure pipeline's current
    /// state plus the regime filter and relative strength results, then
    /// evaluates the Decision Engine. Shared by both Stage 1 and Stage 2 — the
    /// only difference between the stages is what MtfAlignmentResult gets
    /// passed in (Weekly/Daily-only vs full-stack).
    /// </summary>
    private async Task<DecisionEngineResult> EvaluateDecisionEngineAsync(
        Symbol symbol,
        DateOnly asOfDate,
        TrendDirection proposedDirection,
        StructureAnalysisPipelineFactory.Handles dailyPipeline,
        IReadOnlyList<Candle> dailyCandles,
        MultiTimeframe.MtfAlignmentResult mtfResult,
        CancellationToken ct)
    {
        var structureSnapshot = new StructureSnapshot(
            symbol.SymbolId,
            Timeframe.Daily,
            dailyPipeline.StructureBreaks.PrevailingTrend,
            dailyPipeline.SwingPoints.ConfirmedSwings,
            dailyPipeline.StructureBreaks.Breaks,
            dailyPipeline.ImpulseLegs.Legs,
            dailyPipeline.SmcZones.Zones,
            dailyPipeline.SmcEvents.Events);

        // Broad-market regime state and the relative-strength benchmark series
        // are fetched once per scan run (not once per symbol) via
        // IBroadMarketContextProvider — see RunAsync, which populates
        // _currentRunBroadMarketContext before any symbol is scanned. Fall
        // back to an inert Neutral/empty context only if this method is ever
        // called outside a RunAsync pass (e.g. a future unit test constructing
        // the scanner and calling this directly) — never fabricate a
        // non-Neutral trend state with no basis.
        var broadMarketContext = _currentRunBroadMarketContext
            ?? new BroadMarketContext(new BroadMarketState(BroadMarketTrendState.Neutral, BroadMarketTrendState.Neutral, asOfDate), Array.Empty<Candle>(), Array.Empty<Candle>());

        var regimeResult = _regimeFilter.Evaluate(broadMarketContext.RegimeState, proposedDirection);

        var relativeStrength = _relativeStrengthCalculator.Calculate(dailyCandles, broadMarketContext.NiftyMicrocap250Candles, broadMarketContext.Nifty50Candles);

        var latestCandle = dailyCandles[^1];
        var previousCandle = dailyCandles.Count > 1 ? dailyCandles[^2] : null;
        // Band-aware when the NSE feed has this symbol (see
        // ICircuitBandTracker's doc comment on the two detection paths);
        // falls back to the zero-range-only heuristic when it doesn't —
        // _currentRunCircuitBands can itself be null (feed fetch failed for
        // this whole run) or simply not contain this particular symbol.
        var circuitBandFraction = _currentRunCircuitBands is not null && _currentRunCircuitBands.TryGetValue(symbol.NseSymbol, out var band)
            ? band
            : (decimal?)null;
        var circuitState = previousCandle is null
            ? DataQuality.CircuitBandState.None
            : _circuitBandTracker.DetectFromLatestCandle(latestCandle, previousCandle, circuitBandFraction);
        var isCircuitLockedAgainstDirection =
            (proposedDirection == TrendDirection.Bullish && circuitState == DataQuality.CircuitBandState.UpperLocked) ||
            (proposedDirection == TrendDirection.Bearish && circuitState == DataQuality.CircuitBandState.LowerLocked);

        var recentChoch = dailyPipeline.StructureBreaks.Breaks.LastOrDefault(b => b.Kind == StructureBreakKind.CHoCH);
        var hasStructureBreakAgainstDirection = recentChoch is not null && recentChoch.NewDirection != proposedDirection;

        var candlePsychologyMetrics = _candlePsychologyAnalyzer.ComputeMetrics(dailyCandles, currentAtr: null, volumeSma20: null);
        var candlePatterns = _candlePsychologyAnalyzer.DetectPatterns(dailyCandles);

        // FIX: this used to be hardcoded to an empty dictionary with a comment
        // claiming graceful degradation — in practice that meant every
        // indicator-keyed branch in DecisionEngine.LayersPart1/2 (Trend's
        // EMA_20/EMA_50/ADX_14, Momentum's RSI_14/MACD_12_26_9/Stochastic_14_3,
        // Volume's OBV, Volatility's HistVol_20) silently fell through to its
        // "absent" case on every symbol, every run, even though the indicators
        // themselves were being computed correctly in dailyPipeline. Read them
        // back out via the same handle Atr/VolumeSma already used.
        var indicatorValues = dailyPipeline.SnapshotIndicatorValues();

        var input = new DecisionEngineInput(
            symbol.SymbolId,
            asOfDate,
            proposedDirection,
            structureSnapshot,
            indicatorValues,
            mtfResult,
            regimeResult,
            relativeStrength,
            candlePsychologyMetrics,
            candlePatterns,
            DataQualityPassed: true,
            DataQualityFailureReason: null,
            IsCircuitLockedAgainstDirection: isCircuitLockedAgainstDirection,
            HasStructureBreakAgainstDirectionWithinLookback: hasStructureBreakAgainstDirection,
            StructureBreakAgainstDirectionDetail: hasStructureBreakAgainstDirection
                ? $"CHoCH to {recentChoch!.NewDirection} at {recentChoch.Timestamp:O}."
                : null);

        return await Task.FromResult(_decisionEngine.Evaluate(input));
    }
}
