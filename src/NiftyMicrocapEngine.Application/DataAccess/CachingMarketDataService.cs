using Microsoft.Extensions.Logging;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.DataAccess;

/// <summary>
/// Implementation of ICachingMarketDataService. Delta-fetch strategy per
/// symbol/timeframe: read the cached latest timestamp, and if it already
/// covers the requested range, serve entirely from cache with zero network
/// calls. Otherwise fetch only [latestCached+1step, to] from the router,
/// persist the delta (upsert, so overlapping candles from a corporate-action
/// reconciliation pass just get overwritten cleanly), then read the full
/// requested range back from the cache so the caller always gets one
/// consistent series regardless of how much of it was already cached.
///
/// Cache-miss behavior: if nothing is cached at all, fetches the full
/// requested range in one call rather than looping day-by-day — a symbol's
/// first-ever fetch shouldn't be N sequential single-day requests.
/// </summary>
public sealed class CachingMarketDataService : ICachingMarketDataService
{
    private readonly ICandleRepository _candleRepository;
    private readonly IMarketDataRouter _dataRouter;
    private readonly ILogger<CachingMarketDataService> _logger;

    public CachingMarketDataService(ICandleRepository candleRepository, IMarketDataRouter dataRouter, ILogger<CachingMarketDataService> logger)
    {
        _candleRepository = candleRepository;
        _dataRouter = dataRouter;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var latestCached = await _candleRepository.GetLatestTimestampAsync(symbolId, timeframe, ct);

        if (latestCached is null)
        {
            _logger.LogDebug("No cached candles for SymbolId={SymbolId}/{Timeframe} — full fetch.", symbolId, timeframe);
            await FetchAndPersistAsync(symbolId, timeframe, from, to, ct);
        }
        else if (latestCached.Value < to)
        {
            // Delta-fetch: only what's missing beyond the cache. A one-step
            // overlap (StepFor) is intentional — refetching the most recent
            // cached candle catches the case where it was a still-forming bar
            // when last cached and has since closed with a different value,
            // without needing a separate "is this candle final" concept.
            var deltaFrom = latestCached.Value - StepFor(timeframe);
            _logger.LogDebug(
                "Cache covers up to {LatestCached} for SymbolId={SymbolId}/{Timeframe}; fetching delta from {DeltaFrom} to {To}.",
                latestCached, symbolId, timeframe, deltaFrom, to);
            await FetchAndPersistAsync(symbolId, timeframe, deltaFrom, to, ct);
        }
        else
        {
            _logger.LogDebug("Cache already covers the full requested range for SymbolId={SymbolId}/{Timeframe} — serving from cache only.", symbolId, timeframe);
        }

        return await _candleRepository.GetCandlesAsync(symbolId, timeframe, from, to, ct);
    }

    private async Task FetchAndPersistAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var fetchResult = await _dataRouter.GetCandlesAsync(symbolId, timeframe, from, to, ct);
        if (fetchResult.Candles.Count > 0)
        {
            await _candleRepository.SaveCandlesAsync(fetchResult.Candles, ct);
        }
    }

    private static TimeSpan StepFor(Timeframe timeframe) => timeframe switch
    {
        Timeframe.Weekly => TimeSpan.FromDays(7),
        Timeframe.Daily => TimeSpan.FromDays(1),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.M30 => TimeSpan.FromMinutes(30),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromDays(1)
    };
}
