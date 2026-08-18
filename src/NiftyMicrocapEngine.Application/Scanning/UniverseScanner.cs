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

namespace NiftyMicrocapEngine.Application.Scanning;

/// <summary>
/// Implements the section 17 two-stage funnel. Orchestrates every engine built
/// in Phases 1-4: cached market data access, structure pipeline (indicators +
/// SMC), data quality gate, circuit-band tracker, MTF engine, regime filter,
/// relative strength, decision engine, and (for approved candidates) the
/// trade plan builder. This is intentionally the ONLY place in the codebase
/// where all of these are wired together in one pipeline pass — everything
/// else stays independently testable in isolation, per the architecture
/// established in Phases 2-4. Per-symbol scan logic lives in the
/// ScanOneSymbol* partial file.
///
/// Candle access goes through ICachingMarketDataService (cache-first,
/// delta-fetch-then-persist) rather than IMarketDataRouter directly, per
/// section 17's "cached + incrementally-updated" requirement for Stage 1.
/// Broad-market regime state and the two relative-strength benchmark series
/// are fetched ONCE per scan run via IBroadMarketContextProvider (not once
/// per symbol) and reused across every Stage 1/Stage 2 evaluation.
///
/// Both stages run with bounded parallelism (ScannerOptions.MaxDegreeOfParallelism,
/// default 8) rather than sequentially — section 17 targets "well under 5
/// minutes" for 250 symbols, which a strictly sequential loop making live
/// HTTP calls cannot realistically hit. NOTE: every repository write in this
/// class (candle cache persistence, IndicatorValues/MarketStructureEvents)
/// goes through Microsoft.Data.Sqlite, which serializes writes at the
/// database-file level regardless of application-level parallelism — so
/// higher MaxDegreeOfParallelism speeds up the network-bound work (HTTP
/// fetches, indicator computation) but write-heavy phases will still
/// effectively serialize at the SQLite layer. This is expected SQLite
/// behavior, not a bug, and matches the "single-developer/small-team tool"
/// scale this schema was designed for (see MigrationRunner's doc comment).
/// </summary>
public sealed partial class UniverseScanner : IUniverseScanner
{
    private readonly ISymbolRepository _symbolRepository;
    private readonly IUniverseRepository _universeRepository;
    private readonly ICachingMarketDataService _cachingDataService;
    private readonly IBroadMarketContextProvider _broadMarketContextProvider;
    private readonly IDataQualityGate _dataQualityGate;
    private readonly ICircuitBandTracker _circuitBandTracker;
    private readonly INseCircuitBandProvider _nseCircuitBandProvider;
    private readonly IMultiTimeframeEngine _mtfEngine;
    private readonly IRegimeFilter _regimeFilter;
    private readonly IRelativeStrengthCalculator _relativeStrengthCalculator;
    private readonly IDecisionEngine _decisionEngine;
    private readonly ITradePlanBuilder _tradePlanBuilder;
    private readonly ICandlePsychologyAnalyzer _candlePsychologyAnalyzer;
    private readonly IIndicatorValueRepository _indicatorValueRepository;
    private readonly IMarketStructureEventRepository _marketStructureEventRepository;
    private readonly IScanHistoryRepository _scanHistoryRepository;
    private readonly ScannerOptions _scannerOptions;
    private readonly StructureThresholds _structureThresholds;
    private readonly ILogger<UniverseScanner> _logger;

    // Set once per RunAsync call, before any symbol is scanned, and read by
    // every per-symbol Stage 1/Stage 2 evaluation via EvaluateDecisionEngineAsync.
    private BroadMarketContext? _currentRunBroadMarketContext;

    // Same pattern, for the same reason — §6.8's real circuit-band feed
    // changes rarely enough (see NseCircuitBandProvider's own internal
    // caching) that fetching it once per scan run, not once per symbol,
    // is correct, not just an optimization.
    private IReadOnlyDictionary<string, decimal>? _currentRunCircuitBands;

    public UniverseScanner(
        ISymbolRepository symbolRepository,
        IUniverseRepository universeRepository,
        ICachingMarketDataService cachingDataService,
        IBroadMarketContextProvider broadMarketContextProvider,
        IDataQualityGate dataQualityGate,
        ICircuitBandTracker circuitBandTracker,
        INseCircuitBandProvider nseCircuitBandProvider,
        IMultiTimeframeEngine mtfEngine,
        IRegimeFilter regimeFilter,
        IRelativeStrengthCalculator relativeStrengthCalculator,
        IDecisionEngine decisionEngine,
        ITradePlanBuilder tradePlanBuilder,
        ICandlePsychologyAnalyzer candlePsychologyAnalyzer,
        IIndicatorValueRepository indicatorValueRepository,
        IMarketStructureEventRepository marketStructureEventRepository,
        IScanHistoryRepository scanHistoryRepository,
        IOptions<ScannerOptions> scannerOptions,
        IOptions<StructureThresholds> structureThresholds,
        ILogger<UniverseScanner> logger)
    {
        _symbolRepository = symbolRepository;
        _universeRepository = universeRepository;
        _cachingDataService = cachingDataService;
        _broadMarketContextProvider = broadMarketContextProvider;
        _dataQualityGate = dataQualityGate;
        _circuitBandTracker = circuitBandTracker;
        _nseCircuitBandProvider = nseCircuitBandProvider;
        _mtfEngine = mtfEngine;
        _regimeFilter = regimeFilter;
        _relativeStrengthCalculator = relativeStrengthCalculator;
        _decisionEngine = decisionEngine;
        _tradePlanBuilder = tradePlanBuilder;
        _candlePsychologyAnalyzer = candlePsychologyAnalyzer;
        _indicatorValueRepository = indicatorValueRepository;
        _marketStructureEventRepository = marketStructureEventRepository;
        _scanHistoryRepository = scanHistoryRepository;
        _scannerOptions = scannerOptions.Value;
        _structureThresholds = structureThresholds.Value;
        _logger = logger;
    }

    public async Task<ScanRunResult> RunAsync(DateOnly asOfDate, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting scan run for {AsOfDate}.", asOfDate);
        var stage1Sw = System.Diagnostics.Stopwatch.StartNew();

        var snapshot = await _universeRepository.GetLatestSnapshotAsync(ct)
            ?? throw new InvalidOperationException("No universe snapshot available — run universe fetch first.");
        var memberSymbolIds = await _universeRepository.GetMemberSymbolIdsAsync(snapshot.UniverseSnapshotId, ct);

        // Fetched once per run, not once per symbol — reused by every Stage 1
        // and Stage 2 evaluation via EvaluateDecisionEngineAsync.
        _currentRunBroadMarketContext = await _broadMarketContextProvider.GetContextAsync(asOfDate, ct);

        // Same pattern for §6.8's real circuit-band feed. A fetch failure
        // here must not abort the run — NseCircuitBandProvider already
        // degrades gracefully internally (serves its last good cache, or
        // empty), and an empty/missing band per symbol just means
        // EvaluateDecisionEngineAsync falls back to the zero-range-only
        // heuristic for that symbol, exactly as if this feed didn't exist.
        try
        {
            _currentRunCircuitBands = await _nseCircuitBandProvider.GetCircuitBandsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch NSE circuit-band data for this run — falling back to the zero-range heuristic for every symbol.");
            _currentRunCircuitBands = null;
        }

        var stage1ResultsBag = new System.Collections.Concurrent.ConcurrentBag<ScanCandidateResult>();
        var stage1FailureCounter = 0;

        // Bounded parallelism, not unbounded Task.WhenAll over all 250
        // symbols at once — each symbol's Stage 1 pass makes several HTTP
        // calls (Daily + Weekly candle fetches, each already individually
        // rate-limited/retried at the HttpClient level per provider), and
        // firing all 250 concurrently would either overwhelm the rate
        // limiter's queue or make debugging a slow run much harder than a
        // capped-concurrency approach. MaxDegreeOfParallelism is configurable
        // via ScannerOptions rather than a hardcoded magic number, since the
        // right value depends on the deployment's actual network/DB capacity.
        using var stage1Throttle = new SemaphoreSlim(Math.Max(1, _scannerOptions.MaxDegreeOfParallelism));

        var stage1Tasks = memberSymbolIds.Select(async symbolId =>
        {
            await stage1Throttle.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                var symbol = await _symbolRepository.GetBySymbolIdAsync(symbolId, ct);
                if (symbol is null)
                {
                    _logger.LogWarning("SymbolId {SymbolId} is in the universe snapshot but has no Symbols row — skipping.", symbolId);
                    return;
                }

                try
                {
                    var result = await ScanOneSymbolStage1Async(symbol, asOfDate, ct);
                    stage1ResultsBag.Add(result);
                }
                catch (OperationCanceledException)
                {
                    throw; // caller-requested cancellation must propagate, not be swallowed as a per-symbol failure
                }
                catch (Exception ex)
                {
                    // A single symbol's failure (a bad candle, a provider
                    // timeout that exhausted retries, an unexpected data
                    // shape) must not take down the other symbols running
                    // concurrently in this batch. Record it as an exclusion
                    // with the exception message as the reason, so it's
                    // visible in the results rather than silently missing.
                    System.Threading.Interlocked.Increment(ref stage1FailureCounter);
                    _logger.LogError(ex, "Stage 1 scan failed for {NseSymbol} (SymbolId={SymbolId}) — excluding from this run's results.", symbol.NseSymbol, symbol.SymbolId);
                    stage1ResultsBag.Add(new ScanCandidateResult(symbol.SymbolId, symbol.NseSymbol, ScanStage.Stage1CoarseOnly, null, null, true,
                        new[] { $"Unhandled exception during Stage 1 scan: {ex.Message}" }));
                }
            }
            finally
            {
                stage1Throttle.Release();
            }
        });

        await Task.WhenAll(stage1Tasks);

        var stage1Results = stage1ResultsBag.ToList();
        var excludedCount = stage1Results.Count(r => r.ExcludedByDataQualityGate);
        var stage1FailureCount = stage1FailureCounter;

        stage1Sw.Stop();
        _logger.LogInformation(
            "Stage 1 complete: {Scanned} scanned, {Excluded} excluded ({Failures} due to unhandled exceptions), duration {Duration}, max parallelism {MaxDegreeOfParallelism}.",
            stage1Results.Count, excludedCount, stage1FailureCount, stage1Sw.Elapsed, _scannerOptions.MaxDegreeOfParallelism);

        var shortlist = stage1Results
            .Where(r => !r.ExcludedByDataQualityGate && r.DecisionResult is not null)
            .OrderByDescending(r => r.DecisionResult!.ConfidenceScore)
            .Take(_scannerOptions.Stage2ShortlistSize)
            .ToList();

        var stage2Sw = System.Diagnostics.Stopwatch.StartNew();
        var stage2ResultsBag = new System.Collections.Concurrent.ConcurrentBag<ScanCandidateResult>();

        using var stage2Throttle = new SemaphoreSlim(Math.Max(1, _scannerOptions.MaxDegreeOfParallelism));

        var stage2Tasks = shortlist.Select(async candidate =>
        {
            await stage2Throttle.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var refined = await ScanOneSymbolStage2Async(candidate, asOfDate, ct);
                    stage2ResultsBag.Add(refined);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stage 2 scan failed for {NseSymbol} (SymbolId={SymbolId}) — falling back to its Stage 1 result.", candidate.NseSymbol, candidate.SymbolId);
                    // Fall back to the Stage 1 result rather than dropping the
                    // symbol entirely — it already cleared Stage 1's shortlist
                    // cut, so a Stage 2-only failure (e.g. an intraday fetch
                    // timeout) shouldn't erase that from the final ranking.
                    stage2ResultsBag.Add(candidate);
                }
            }
            finally
            {
                stage2Throttle.Release();
            }
        });

        await Task.WhenAll(stage2Tasks);

        var stage2Results = stage2ResultsBag.ToList();
        stage2Sw.Stop();

        var rankedStage2 = RankStage2Results(stage2Results);

        _logger.LogInformation(
            "Scan run complete for {AsOfDate}: Stage 2 produced {Count} ranked result(s), duration {Stage1Duration} + {Stage2Duration}.",
            asOfDate, rankedStage2.Count, stage1Sw.Elapsed, stage2Sw.Elapsed);

        // §23/§25's benchmark deliverable needs Stage-1/Stage-2 timings tracked
        // over time, not just printed once to console — see ScanHistoryRecord's
        // doc comment. A failure here shouldn't fail the scan itself (the
        // caller's actual results are already computed), so it's logged rather
        // than allowed to throw out of RunAsync.
        try
        {
            await _scanHistoryRepository.SaveAsync(new ScanHistoryRecord(
                ScanId: 0,
                RunAt: DateTimeOffset.UtcNow,
                Stage1Count: stage1Results.Count,
                Stage2Count: rankedStage2.Count,
                Stage1DurationMs: (long)stage1Sw.Elapsed.TotalMilliseconds,
                Stage2DurationMs: (long)stage2Sw.Elapsed.TotalMilliseconds), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist ScanHistory for this run — the scan result itself is unaffected.");
        }

        return new ScanRunResult(
            asOfDate,
            stage1Results.Count,
            excludedCount,
            _scannerOptions.Stage2ShortlistSize,
            stage1Results,
            rankedStage2,
            stage1Sw.Elapsed,
            stage2Sw.Elapsed);
    }

    /// <summary>
    /// Rank Stage-2 output per section 17: confidence, then momentum, then
    /// trend, then risk-reward, then expected return, then relative strength —
    /// in that priority order, each a tiebreaker for the one before it.
    /// </summary>
    private static IReadOnlyList<ScanCandidateResult> RankStage2Results(IReadOnlyList<ScanCandidateResult> results)
    {
        var scored = results.Where(r => r.DecisionResult is not null)
            .OrderByDescending(r => r.DecisionResult!.ConfidenceScore)
            .ThenByDescending(r => LayerContribution(r.DecisionResult!, "Momentum"))
            .ThenByDescending(r => LayerContribution(r.DecisionResult!, "Trend"))
            .ThenByDescending(r => r.TradePlan?.RiskRewardRatio ?? 0m)
            .ThenByDescending(r => ExpectedReturn(r.TradePlan))
            .ThenByDescending(r => LayerContribution(r.DecisionResult!, "Relative Strength & Regime"))
            .ToList();

        var unscored = results.Where(r => r.DecisionResult is null).ToList();

        return scored.Concat(unscored).ToList();
    }

    private static decimal LayerContribution(DecisionEngineResult result, string layerName) =>
        result.LayerScores.FirstOrDefault(l => l.LayerName == layerName)?.ClampedContribution ?? 0m;

    /// <summary>Expected return proxy: reward-per-share to Target1 as a fraction of entry, the same basis as RiskPercent for direct comparability.</summary>
    private static decimal ExpectedReturn(TradePlan? plan) =>
        plan is null || plan.Entry == 0 ? 0m : Math.Abs(plan.Target1 - plan.Entry) / plan.Entry;
}
