using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Application.Scanning;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Web;

public static class DashboardEndpoints
{
    /// <summary>
    /// §20's "Still open" gap: RunScan/GetDrillDown/GetChart each re-ran the
    /// Scanner (a full 250-symbol Stage 1 + Stage 2 pass) from scratch on
    /// every single API call — so a user clicking through 5 symbols in the
    /// Symbol Drill-Down view triggered 5 full universe scans. This caches
    /// the last ScanRunResult per as-of date in memory (IMemoryCache is
    /// Singleton-scoped, so it survives across the per-request Scoped
    /// IUniverseScanner instances) so GetDrillDown reads a symbol's result
    /// out of an already-computed scan instead of recomputing the whole
    /// universe just to answer one lookup.
    ///
    /// 15-minute expiry, not indefinite: a scan reflects point-in-time
    /// market data, and this dashboard has no push-based invalidation (e.g.
    /// on reconciliation correcting a candle) — a bounded TTL means a stale
    /// cache entry ages out on its own within a reasonable window rather
    /// than needing an explicit invalidation hook this pass doesn't build.
    /// RunScan accepts an explicit refresh=true query param for anyone who
    /// wants a guaranteed-fresh scan sooner than that.
    ///
    /// A SemaphoreSlim guards the miss path specifically to avoid a cache
    /// stampede: several dashboard tabs/requests hitting an expired or
    /// never-populated entry at once would otherwise each independently
    /// trigger their own full 250-symbol scan. One shared lock across all
    /// as-of dates, not per-key — a deliberate simplification: this
    /// dashboard is realistically used against "today" almost exclusively,
    /// so the rare case of two different dates briefly serializing behind
    /// each other costs little next to the complexity of per-key locking.
    /// </summary>
    private static readonly TimeSpan ScanCacheDuration = TimeSpan.FromMinutes(15);
    private static readonly SemaphoreSlim ScanCacheLock = new(1, 1);

    private static async Task<ScanRunResult> GetOrRunScanAsync(IUniverseScanner scanner, IMemoryCache cache, DateOnly asOfDate, bool forceRefresh, CancellationToken ct)
    {
        var cacheKey = $"scan-result:{asOfDate:yyyy-MM-dd}";

        if (!forceRefresh && cache.TryGetValue(cacheKey, out ScanRunResult? cached) && cached is not null)
        {
            return cached;
        }

        await ScanCacheLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock — a concurrent request may
            // have already populated the cache while this one was waiting,
            // in which case there's no need to run a second full scan.
            if (!forceRefresh && cache.TryGetValue(cacheKey, out ScanRunResult? cachedAfterLock) && cachedAfterLock is not null)
            {
                return cachedAfterLock;
            }

            var result = await scanner.RunAsync(asOfDate, ct);
            cache.Set(cacheKey, result, ScanCacheDuration);
            return result;
        }
        finally
        {
            ScanCacheLock.Release();
        }
    }

    public static async Task<IResult> RunScan(IUniverseScanner scanner, IMemoryCache cache, string? date, bool? refresh, CancellationToken ct)
    {
        var asOfDate = ParseOrToday(date);
        var result = await GetOrRunScanAsync(scanner, cache, asOfDate, forceRefresh: refresh == true, ct);
        return Results.Ok(DashboardMapping.ToScanResponse(result));
    }

    public static async Task<IResult> GetDrillDown(int symbolId, IUniverseScanner scanner, IMemoryCache cache, string? date, CancellationToken ct)
    {
        var asOfDate = ParseOrToday(date);
        var result = await GetOrRunScanAsync(scanner, cache, asOfDate, forceRefresh: false, ct);

        var candidate = result.Stage2Results.FirstOrDefault(r => r.SymbolId == symbolId);
        candidate ??= result.Stage1Results.FirstOrDefault(r => r.SymbolId == symbolId);

        if (candidate is null) return Results.NotFound();
        return Results.Ok(DashboardMapping.ToDrillDownResponse(candidate));
    }

    public static async Task<IResult> RunReconciliation(ICorporateActionReconciliationJob job)
    {
        var result = await job.RunAsync();
        return Results.Ok(new
        {
            symbolsChecked = result.SymbolsChecked,
            symbolsFailed = result.SymbolsFailed,
            overwriteCount = result.Overwrites.Count,
            durationMs = result.Duration.TotalMilliseconds
        });
    }

    /// <summary>
    /// §20.2's Charting Terminal data source: candles plus structure/SMC
    /// overlay data for one symbol/timeframe. Re-runs the structure pipeline
    /// fresh per request rather than reading persisted IndicatorValues/
    /// MarketStructureEvents — the same pragmatic choice RunScan/GetDrillDown
    /// already make (see this file's own "Still open" note in the README),
    /// so a chart re-fetch after a reconciliation correction always reflects
    /// current cached candle data rather than a possibly-stale persisted
    /// snapshot.
    /// </summary>
    public static async Task<IResult> GetChart(
        int symbolId,
        string? timeframe,
        string? date,
        int? lookbackDays,
        ISymbolRepository symbolRepository,
        ICachingMarketDataService cachingDataService,
        IOptions<StructureThresholds> structureThresholds)
    {
        var symbol = await symbolRepository.GetBySymbolIdAsync(symbolId);
        if (symbol is null) return Results.NotFound();

        var tf = ParseTimeframeOrDefault(timeframe);
        var asOfDate = ParseOrToday(date);
        var lookback = lookbackDays is > 0 ? lookbackDays.Value : 365;

        var from = asOfDate.AddDays(-lookback).ToUtcDateTimeOffset(TimeOnly.MinValue);
        var to = asOfDate.ToUtcDateTimeOffset(TimeOnly.MaxValue);
        var candles = (await cachingDataService.GetCandlesAsync(symbolId, tf, from, to))
            .OrderBy(c => c.Timestamp).ToList();

        var pipeline = StructureAnalysisPipelineFactory.Create(symbolId, tf, structureThresholds.Value);
        foreach (var candle in candles)
        {
            await pipeline.Pipeline.RunAsync(candle);
        }

        return Results.Ok(ChartMapping.ToChartResponse(symbol, tf, asOfDate, candles, pipeline));
    }

    private static Timeframe ParseTimeframeOrDefault(string? timeframe) =>
        Enum.TryParse<Timeframe>(timeframe, ignoreCase: true, out var parsed) ? parsed : Timeframe.Daily;

    private static DateOnly ParseOrToday(string? date)
    {
        if (date is not null && DateOnly.TryParse(date, out var parsed)) return parsed;
        return DateOnly.FromDateTime(DateTime.UtcNow);
    }
}
