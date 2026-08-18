using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Tests.Scanning;

/// <summary>Delegates straight to a router without any caching — sufficient for tests that don't care about cache behavior, only Scanner orchestration.</summary>
public sealed class FakeCachingMarketDataService : ICachingMarketDataService
{
    private readonly IMarketDataRouter _router;

    public FakeCachingMarketDataService(IMarketDataRouter router) => _router = router;

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var result = await _router.GetCandlesAsync(symbolId, timeframe, from, to, ct);
        return result.Candles;
    }
}
