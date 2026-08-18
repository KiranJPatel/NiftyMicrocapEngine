using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.DataQuality;
using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.MultiTimeframe;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Backtesting;

public sealed class WalkForwardBacktester : IWalkForwardBacktester
{
    private readonly ISymbolRepository _symbolRepository;
    private readonly IUniverseRepository _universeRepository;
    private readonly ICachingMarketDataService _cachingDataService;
    private readonly IBroadMarketContextProvider _broadMarketContextProvider;
    private readonly IDataQualityGate _dataQualityGate;
    private readonly ICircuitBandTracker _circuitBandTracker;
    private readonly IMultiTimeframeEngine _mtfEngine;
    private readonly IRegimeFilter _regimeFilter;
    private readonly IRelativeStrengthCalculator _relativeStrengthCalculator;
    private readonly IDecisionEngine _decisionEngine;
    private readonly ITradePlanBuilder _tradePlanBuilder;
    private readonly ICandlePsychologyAnalyzer _candlePsychologyAnalyzer;
    private readonly StructureThresholds _structureThresholds;
    private readonly ILogger<WalkForwardBacktester> _logger;

    public WalkForwardBacktester(
        ISymbolRepository symbolRepository,
        IUniverseRepository universeRepository,
        ICachingMarketDataService cachingDataService,
        IBroadMarketContextProvider broadMarketContextProvider,
        IDataQualityGate dataQualityGate,
        ICircuitBandTracker circuitBandTracker,
        IMultiTimeframeEngine mtfEngine,
        IRegimeFilter regimeFilter,
        IRelativeStrengthCalculator relativeStrengthCalculator,
        IDecisionEngine decisionEngine,
        ITradePlanBuilder tradePlanBuilder,
        ICandlePsychologyAnalyzer candlePsychologyAnalyzer,
        IOptions<StructureThresholds> structureThresholds,
        ILogger<WalkForwardBacktester> logger)
    {
        _symbolRepository = symbolRepository;
        _universeRepository = universeRepository;
        _cachingDataService = cachingDataService;
        _broadMarketContextProvider = broadMarketContextProvider;
        _dataQualityGate = dataQualityGate;
        _circuitBandTracker = circuitBandTracker;
        _mtfEngine = mtfEngine;
        _regimeFilter = regimeFilter;
        _relativeStrengthCalculator = relativeStrengthCalculator;
        _decisionEngine = decisionEngine;
        _tradePlanBuilder = tradePlanBuilder;
        _candlePsychologyAnalyzer = candlePsychologyAnalyzer;
        _structureThresholds = structureThresholds.Value;
        _logger = logger;
    }

    public async Task<BacktestReport> RunAsync(BacktestRequest request, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var symbolIds = request.SymbolIds;
        if (symbolIds is null || symbolIds.Count == 0)
        {
            var snapshot = await _universeRepository.GetLatestSnapshotAsync(ct)
                ?? throw new InvalidOperationException("No universe snapshot available — run universe fetch first.");
            symbolIds = (await _universeRepository.GetMemberSymbolIdsAsync(snapshot.UniverseSnapshotId, ct))
                .Take(request.MaxSymbols).ToList();
        }

        var allTrades = new List<BacktestTradeOutcome>();
        var totalAsOfDates = 0;

        foreach (var symbolId in symbolIds)
        {
            ct.ThrowIfCancellationRequested();

            var symbol = await _symbolRepository.GetBySymbolIdAsync(symbolId, ct);
            if (symbol is null)
            {
                _logger.LogWarning("Backtest: SymbolId {SymbolId} has no Symbols row — skipping.", symbolId);
                continue;
            }

            try
            {
                var (evaluatedDates, trades) = await WalkOneSymbolAsync(symbol, request, ct);
                totalAsOfDates += evaluatedDates;
                allTrades.AddRange(trades);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Same principle as the live Scanner: one symbol's failure
                // (thin history, a provider gap) must not abort the whole
                // backtest run.
                _logger.LogError(ex, "Backtest walk failed for {NseSymbol} (SymbolId={SymbolId}) — excluding from this run.", symbol.NseSymbol, symbol.SymbolId);
            }
        }

        var bucketStats = BuildBucketStats(allTrades);

        sw.Stop();
        return new BacktestReport(
            request,
            DateOnly.FromDateTime(DateTime.UtcNow),
            symbolIds.Count,
            totalAsOfDates,
            allTrades.Count,
            bucketStats,
            allTrades,
            sw.Elapsed);
    }

    /// <summary>
    /// Mirrors UniverseScanner.Stage1.cs + DecisionInput.cs + TradePlan.cs
    /// step for step, but (a) loops over many historical as-of dates instead
    /// of one "today", (b) fetches broad-market/regime context fresh per
    /// as-of date rather than once per run, and (c) simulates a forward
    /// outcome for any Buy/StrongBuy signal instead of returning it for live
    /// display.
    /// </summary>
    private async Task<(int EvaluatedDates, List<BacktestTradeOutcome> Trades)> WalkOneSymbolAsync(
        Symbol symbol, BacktestRequest request, CancellationToken ct)
    {
        // Fetch once for the whole symbol: 2 years of warmup before
        // StartDate through EndDate, so every simulated as-of date has
        // enough trailing history for indicator/structure warmup, and
        // there's forward data available for outcome simulation right up to
        // EndDate.
        var fetchFrom = request.StartDate.AddYears(-2).ToUtcDateTimeOffset(TimeOnly.MinValue);
        var fetchTo = request.EndDate.ToUtcDateTimeOffset(TimeOnly.MaxValue);

        var dailyAll = (await _cachingDataService.GetCandlesAsync(symbol.SymbolId, Timeframe.Daily, fetchFrom, fetchTo, ct))
            .OrderBy(c => c.Timestamp).ToList();
        var weeklyAll = (await _cachingDataService.GetCandlesAsync(symbol.SymbolId, Timeframe.Weekly, fetchFrom, fetchTo, ct))
            .OrderBy(c => c.Timestamp).ToList();

        var tradingDates = dailyAll.Select(c => DateOnly.FromDateTime(c.Timestamp.UtcDateTime)).ToList();
        var windowIndices = Enumerable.Range(0, tradingDates.Count)
            .Where(i => tradingDates[i] >= request.StartDate && tradingDates[i] <= request.EndDate)
            .Where((_, ordinal) => ordinal % Math.Max(1, request.CadenceTradingDays) == 0)
            .ToList();

        var trades = new List<BacktestTradeOutcome>();

        foreach (var dateIndex in windowIndices)
        {
            ct.ThrowIfCancellationRequested();

            var asOfDate = tradingDates[dateIndex];
            var dailySlice = dailyAll.Take(dateIndex + 1).ToList();
            if (dailySlice.Count < 60) continue; // not enough warmup for meaningful indicators/structure yet

            var qualityResult = _dataQualityGate.Evaluate(dailySlice, dailySlice.Select(c => DateOnly.FromDateTime(c.Timestamp.UtcDateTime)).ToList());
            if (!qualityResult.Passed) continue;

            var weeklySlice = weeklyAll.Where(c => DateOnly.FromDateTime(c.Timestamp.UtcDateTime) <= asOfDate).ToList();

            var dailyPipeline = StructureAnalysisPipelineFactory.Create(symbol.SymbolId, Timeframe.Daily, _structureThresholds);
            foreach (var candle in dailySlice) await dailyPipeline.Pipeline.RunAsync(candle, ct);

            var weeklyPipeline = weeklySlice.Count > 0 ? StructureAnalysisPipelineFactory.Create(symbol.SymbolId, Timeframe.Weekly, _structureThresholds) : null;
            if (weeklyPipeline is not null)
            {
                foreach (var candle in weeklySlice) await weeklyPipeline.Pipeline.RunAsync(candle, ct);
            }

            var proposedDirection = dailyPipeline.StructureBreaks.PrevailingTrend == TrendDirection.Bearish ? TrendDirection.Bearish : TrendDirection.Bullish;

            var mtfSignals = new List<TimeframeSignal>
            {
                new(Timeframe.Weekly, weeklyPipeline?.StructureBreaks.PrevailingTrend ?? TrendDirection.Ranging, weeklyPipeline is not null),
                new(Timeframe.Daily, dailyPipeline.StructureBreaks.PrevailingTrend, true),
                new(Timeframe.H1, TrendDirection.Ranging, false),
                new(Timeframe.M30, TrendDirection.Ranging, false),
                new(Timeframe.M15, TrendDirection.Ranging, false)
            };
            var mtfResult = _mtfEngine.Evaluate(mtfSignals, proposedDirection);

            var broadMarketContext = await _broadMarketContextProvider.GetContextAsync(asOfDate, ct);
            var regimeResult = _regimeFilter.Evaluate(broadMarketContext.RegimeState, proposedDirection);
            var relativeStrength = _relativeStrengthCalculator.Calculate(dailySlice, broadMarketContext.NiftyMicrocap250Candles, broadMarketContext.Nifty50Candles);

            var latestCandle = dailySlice[^1];
            var previousCandle = dailySlice.Count > 1 ? dailySlice[^2] : null;
            var circuitState = previousCandle is null ? CircuitBandState.None : _circuitBandTracker.DetectFromLatestCandle(latestCandle, previousCandle);
            var isCircuitLockedAgainstDirection =
                (proposedDirection == TrendDirection.Bullish && circuitState == CircuitBandState.UpperLocked) ||
                (proposedDirection == TrendDirection.Bearish && circuitState == CircuitBandState.LowerLocked);

            var recentChoch = dailyPipeline.StructureBreaks.Breaks.LastOrDefault(b => b.Kind == StructureBreakKind.CHoCH);
            var hasStructureBreakAgainstDirection = recentChoch is not null && recentChoch.NewDirection != proposedDirection;

            var candlePsychologyMetrics = _candlePsychologyAnalyzer.ComputeMetrics(dailySlice, currentAtr: null, volumeSma20: null);
            var candlePatterns = _candlePsychologyAnalyzer.DetectPatterns(dailySlice);

            var structureSnapshot = new StructureSnapshot(
                symbol.SymbolId, Timeframe.Daily, dailyPipeline.StructureBreaks.PrevailingTrend,
                dailyPipeline.SwingPoints.ConfirmedSwings, dailyPipeline.StructureBreaks.Breaks,
                dailyPipeline.ImpulseLegs.Legs, dailyPipeline.SmcZones.Zones, dailyPipeline.SmcEvents.Events);

            var input = new DecisionEngineInput(
                symbol.SymbolId, asOfDate, proposedDirection, structureSnapshot,
                dailyPipeline.SnapshotIndicatorValues(), mtfResult, regimeResult, relativeStrength,
                candlePsychologyMetrics, candlePatterns,
                DataQualityPassed: true, DataQualityFailureReason: null,
                IsCircuitLockedAgainstDirection: isCircuitLockedAgainstDirection,
                HasStructureBreakAgainstDirectionWithinLookback: hasStructureBreakAgainstDirection,
                StructureBreakAgainstDirectionDetail: hasStructureBreakAgainstDirection ? $"CHoCH to {recentChoch!.NewDirection} at {recentChoch.Timestamp:O}." : null);

            var decisionResult = _decisionEngine.Evaluate(input);

            if (decisionResult.Outcome is DecisionOutcome.Buy or DecisionOutcome.StrongBuy)
            {
                var plan = BuildTradePlan(dailyPipeline, dailySlice, proposedDirection);
                var forwardCandles = dailyAll.Skip(dateIndex + 1).ToList();
                var outcome = BacktestOutcomeSimulator.Simulate(
                    symbol.SymbolId, symbol.NseSymbol, asOfDate, decisionResult, plan, proposedDirection,
                    forwardCandles, request.MaxHoldingTradingDaysFallback);
                trades.Add(outcome);
            }
        }

        return (windowIndices.Count, trades);
    }

    /// <summary>Duplicated from UniverseScanner.TradePlan.cs's BuildTradePlanFor/FindNextZonePrice (both private there) rather than refactored into a shared helper, to avoid touching a working, already-tested class for this addition. Keep in sync if that logic changes.</summary>
    private TradePlan BuildTradePlan(StructureAnalysisPipelineFactory.Handles dailyPipeline, IReadOnlyList<Candle> dailyCandles, TrendDirection direction)
    {
        var latestCandle = dailyCandles[^1];
        var isLong = direction == TrendDirection.Bullish;

        var structuralStop = isLong
            ? dailyPipeline.SwingPoints.ConfirmedSwings.LastOrDefault(s => s.Type == SwingType.Low)?.Price
            : dailyPipeline.SwingPoints.ConfirmedSwings.LastOrDefault(s => s.Type == SwingType.High)?.Price;

        var nextZone = FindNextZonePrice(dailyPipeline, latestCandle, isLong);

        var trailingImpulseLegDurations = dailyPipeline.ImpulseLegs.Legs
            .Where(l => l.Kind == LegKind.Impulse && l.EndTimestamp >= latestCandle.Timestamp.AddMonths(-6))
            .Select(l => l.EndTimestamp - l.StartTimestamp)
            .ToList();

        var invalidationDescription = structuralStop is not null
            ? "Structure break beyond " + structuralStop
            : "No confirmed structural invalidation level available";

        var request = new TradePlanRequest(direction, latestCandle.Close, structuralStop, dailyPipeline.Atr.CurrentValue ?? 0m, nextZone, invalidationDescription, trailingImpulseLegDurations);
        return _tradePlanBuilder.Build(request);
    }

    private static decimal? FindNextZonePrice(StructureAnalysisPipelineFactory.Handles dailyPipeline, Candle latestCandle, bool isLong)
    {
        if (isLong)
        {
            var supplyZones = dailyPipeline.SmcZones.Zones
                .Where(z => z.Kind == ZoneKind.SupplyZone || z.Kind == ZoneKind.OrderBlockBearish)
                .Where(z => z.Status != ZoneStatus.FullyMitigated && z.LowerBound > latestCandle.Close)
                .OrderBy(z => z.LowerBound).ToList();
            return supplyZones.Count > 0 ? supplyZones[0].LowerBound : (decimal?)null;
        }

        var demandZones = dailyPipeline.SmcZones.Zones
            .Where(z => z.Kind == ZoneKind.DemandZone || z.Kind == ZoneKind.OrderBlockBullish)
            .Where(z => z.Status != ZoneStatus.FullyMitigated && z.UpperBound < latestCandle.Close)
            .OrderByDescending(z => z.UpperBound).ToList();
        return demandZones.Count > 0 ? demandZones[0].UpperBound : (decimal?)null;
    }

    private static IReadOnlyList<BacktestBucketStats> BuildBucketStats(IReadOnlyList<BacktestTradeOutcome> trades)
    {
        var buckets = new List<BacktestBucketStats>();
        foreach (var decision in new[] { DecisionOutcome.StrongBuy, DecisionOutcome.Buy })
        {
            var group = trades.Where(t => t.Decision == decision).ToList();
            var simulated = group.Where(t => t.ResultKind != BacktestOutcomeKind.InsufficientForwardData).ToList();
            var wins = simulated.Count(t => t.RMultiple > 0);
            var losses = simulated.Count(t => t.RMultiple <= 0 && t.ResultKind != BacktestOutcomeKind.TimedOut);
            var timedOut = simulated.Count(t => t.ResultKind == BacktestOutcomeKind.TimedOut);
            var winRate = simulated.Count > 0 ? (decimal)wins / simulated.Count : 0m;
            var avgR = simulated.Count > 0 ? simulated.Average(t => t.RMultiple) : 0m;

            var avgWinR = simulated.Where(t => t.RMultiple > 0).Select(t => t.RMultiple).DefaultIfEmpty(0m).Average();
            var avgLossR = simulated.Where(t => t.RMultiple <= 0).Select(t => t.RMultiple).DefaultIfEmpty(0m).Average();
            // NOTE (found while hand-verifying a sample report against this
            // exact code): expectedR is algebraically identical to avgR
            // above, not just numerically close — winRate*avgWinR always
            // equals sumOfWinningR/N, and (1-winRate)*avgLossR always
            // equals sumOfLosingR/N (every trade is >0 or <=0 by
            // trichotomy, so their denominators are complementary and sum
            // to N), so the two terms sum to avgR exactly. This is still
            // the textbook-correct expected-value formula — it's only
            // redundant with avgR because both are derived from the SAME
            // realized sample here; it becomes genuinely informative once
            // win-rate and payoff are estimated independently (e.g.
            // testing a hypothetical win-rate shift against the realized
            // payoff ratio) rather than recomputed from one trade set.
            var expectedR = winRate * avgWinR + (1 - winRate) * avgLossR;

            buckets.Add(new BacktestBucketStats(decision, group.Count, simulated.Count, wins, losses, timedOut, winRate, avgR, expectedR));
        }
        return buckets;
    }
}
