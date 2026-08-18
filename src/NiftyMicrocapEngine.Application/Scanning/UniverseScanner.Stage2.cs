using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.MultiTimeframe;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Scanning;

public sealed partial class UniverseScanner
{
    /// <summary>
    /// Stage 2: adds H1/M30/M15 confirmation for the Stage-1 shortlist only
    /// (section 17). Re-runs the full pipeline with intraday data now
    /// available, so the MTF engine's alignment score reflects the complete
    /// stack rather than Stage 1's Weekly/Daily-only renormalized version.
    /// </summary>
    private async Task<ScanCandidateResult> ScanOneSymbolStage2Async(ScanCandidateResult stage1Result, DateOnly asOfDate, CancellationToken ct)
    {
        var to = asOfDate.ToUtcDateTimeOffset(TimeOnly.MaxValue);
        var dailyFrom = asOfDate.AddYears(-2).ToUtcDateTimeOffset(TimeOnly.MinValue);
        var intradayFrom = asOfDate.AddDays(-30).ToUtcDateTimeOffset(TimeOnly.MinValue);

        var symbol = await _symbolRepository.GetBySymbolIdAsync(stage1Result.SymbolId, ct);
        if (symbol is null) return stage1Result;

        var dailyCandlesRaw = await _cachingDataService.GetCandlesAsync(symbol.SymbolId, Timeframe.Daily, dailyFrom, to, ct);
        var dailyCandles = dailyCandlesRaw.OrderBy(c => c.Timestamp).ToList();
        var weeklyCandlesRaw = await _cachingDataService.GetCandlesAsync(symbol.SymbolId, Timeframe.Weekly, dailyFrom, to, ct);
        var weeklyCandles = weeklyCandlesRaw.OrderBy(c => c.Timestamp).ToList();

        var h1Candles = await _cachingDataService.GetCandlesAsync(symbol.SymbolId, Timeframe.H1, intradayFrom, to, ct);
        var m30Candles = await _cachingDataService.GetCandlesAsync(symbol.SymbolId, Timeframe.M30, intradayFrom, to, ct);
        var m15Candles = await _cachingDataService.GetCandlesAsync(symbol.SymbolId, Timeframe.M15, intradayFrom, to, ct);

        var dailyPipeline = StructureAnalysisPipelineFactory.Create(symbol.SymbolId, Timeframe.Daily, _structureThresholds);
        foreach (var candle in dailyCandles) await dailyPipeline.Pipeline.RunAsync(candle, ct);

        var weeklyPipeline = StructureAnalysisPipelineFactory.Create(symbol.SymbolId, Timeframe.Weekly, _structureThresholds);
        foreach (var candle in weeklyCandles) await weeklyPipeline.Pipeline.RunAsync(candle, ct);

        var proposedDirection = dailyPipeline.StructureBreaks.PrevailingTrend == TrendDirection.Bearish ? TrendDirection.Bearish : TrendDirection.Bullish;

        var (h1Trend, h1Available) = await RunIntradayPipelineAsync(symbol.SymbolId, Timeframe.H1, h1Candles, ct);
        var (m30Trend, m30Available) = await RunIntradayPipelineAsync(symbol.SymbolId, Timeframe.M30, m30Candles, ct);
        var (m15Trend, m15Available) = await RunIntradayPipelineAsync(symbol.SymbolId, Timeframe.M15, m15Candles, ct);

        // section 6.4: on broker failure with fallback, a reduced-lookback
        // series is still usable but must be flagged — that flagging happens
        // at the router/repository layer (DataQualityFlags), not re-derived here.

        var mtfSignals = new List<TimeframeSignal>
        {
            new(Timeframe.Weekly, weeklyPipeline.StructureBreaks.PrevailingTrend, weeklyCandles.Count > 0),
            new(Timeframe.Daily, dailyPipeline.StructureBreaks.PrevailingTrend, true),
            new(Timeframe.H1, h1Trend, h1Available),
            new(Timeframe.M30, m30Trend, m30Available),
            new(Timeframe.M15, m15Trend, m15Available)
        };
        var mtfResult = _mtfEngine.Evaluate(mtfSignals, proposedDirection);

        var latestDailyTimestamp = dailyCandles[^1].Timestamp;
        await PersistPipelineOutputAsync(symbol.SymbolId, Timeframe.Daily, latestDailyTimestamp, dailyPipeline, _indicatorValueRepository, _marketStructureEventRepository, ct);

        var decisionResult = await EvaluateDecisionEngineAsync(symbol, asOfDate, proposedDirection, dailyPipeline, dailyCandles, mtfResult, ct);

        TradePlan? tradePlan = null;
        if (decisionResult.Outcome is DecisionOutcome.Buy or DecisionOutcome.StrongBuy)
        {
            tradePlan = BuildTradePlanFor(dailyPipeline, dailyCandles, proposedDirection);
        }

        return new ScanCandidateResult(symbol.SymbolId, symbol.NseSymbol, ScanStage.Stage2FineConfirmed, decisionResult, tradePlan, false, Array.Empty<string>());
    }

    private async Task<(TrendDirection Trend, bool Available)> RunIntradayPipelineAsync(int symbolId, Timeframe timeframe, IReadOnlyList<Domain.Candle> candles, CancellationToken ct)
    {
        if (candles.Count == 0) return (TrendDirection.Ranging, false);

        var pipeline = StructureAnalysisPipelineFactory.Create(symbolId, timeframe, _structureThresholds);
        foreach (var candle in candles.OrderBy(c => c.Timestamp)) await pipeline.Pipeline.RunAsync(candle, ct);

        return (pipeline.StructureBreaks.PrevailingTrend, true);
    }
}
