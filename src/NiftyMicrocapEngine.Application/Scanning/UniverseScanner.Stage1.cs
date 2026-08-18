using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.MultiTimeframe;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Scanning;

public sealed partial class UniverseScanner
{
    /// <summary>
    /// Stage 1: Daily/Weekly only, per section 17. Fetches candles via the
    /// router, runs the full structure/indicator pipeline, evaluates the
    /// Decision Engine with an MTF result built from ONLY Weekly+Daily
    /// (H1/M30/M15 explicitly marked unavailable, so the MTF engine's
    /// renormalization spreads their weight across Weekly/Daily rather than
    /// counting them as misaligned) — this is what makes Stage 1 "coarse."
    /// </summary>
    private async Task<ScanCandidateResult> ScanOneSymbolStage1Async(Symbol symbol, DateOnly asOfDate, CancellationToken ct)
    {
        var to = asOfDate.ToUtcDateTimeOffset(TimeOnly.MaxValue);
        var from = asOfDate.AddYears(-2).ToUtcDateTimeOffset(TimeOnly.MinValue);

        var dailyCandlesRaw = await _cachingDataService.GetCandlesAsync(symbol.SymbolId, Timeframe.Daily, from, to, ct);
        var dailyCandles = dailyCandlesRaw.OrderBy(c => c.Timestamp).ToList();

        if (dailyCandles.Count == 0)
        {
            return new ScanCandidateResult(symbol.SymbolId, symbol.NseSymbol, ScanStage.Stage1CoarseOnly, null, null, true,
                new[] { "No Daily candle data returned from any provider." });
        }

        var expectedTradingDays = dailyCandles.Select(c => DateOnly.FromDateTime(c.Timestamp.UtcDateTime)).ToList();
        var qualityResult = _dataQualityGate.Evaluate(dailyCandles, expectedTradingDays);
        if (!qualityResult.Passed)
        {
            return new ScanCandidateResult(symbol.SymbolId, symbol.NseSymbol, ScanStage.Stage1CoarseOnly, null, null, true, qualityResult.FailureReasons);
        }

        var weeklyCandlesRaw = await _cachingDataService.GetCandlesAsync(symbol.SymbolId, Timeframe.Weekly, from, to, ct);
        var weeklyCandles = weeklyCandlesRaw.OrderBy(c => c.Timestamp).ToList();

        var dailyPipeline = StructureAnalysisPipelineFactory.Create(symbol.SymbolId, Timeframe.Daily, _structureThresholds);
        foreach (var candle in dailyCandles) await dailyPipeline.Pipeline.RunAsync(candle, ct);

        var weeklyPipeline = weeklyCandles.Count > 0 ? StructureAnalysisPipelineFactory.Create(symbol.SymbolId, Timeframe.Weekly, _structureThresholds) : null;
        if (weeklyPipeline is not null)
        {
            foreach (var candle in weeklyCandles) await weeklyPipeline.Pipeline.RunAsync(candle, ct);
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

        var latestDailyTimestamp = dailyCandles[^1].Timestamp;
        await PersistPipelineOutputAsync(symbol.SymbolId, Timeframe.Daily, latestDailyTimestamp, dailyPipeline, _indicatorValueRepository, _marketStructureEventRepository, ct);

        var decisionResult = await EvaluateDecisionEngineAsync(symbol, asOfDate, proposedDirection, dailyPipeline, dailyCandles, mtfResult, ct);

        TradePlan? tradePlan = null;
        if (decisionResult.Outcome is DecisionOutcome.Buy or DecisionOutcome.StrongBuy)
        {
            tradePlan = BuildTradePlanFor(dailyPipeline, dailyCandles, proposedDirection);
        }

        return new ScanCandidateResult(symbol.SymbolId, symbol.NseSymbol, ScanStage.Stage1CoarseOnly, decisionResult, tradePlan, false, Array.Empty<string>());
    }
}
