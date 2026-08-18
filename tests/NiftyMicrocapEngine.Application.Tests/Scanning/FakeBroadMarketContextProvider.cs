using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Tests.Scanning;

public sealed class FakeBroadMarketContextProvider : IBroadMarketContextProvider
{
    private readonly BroadMarketContext _context;

    public FakeBroadMarketContextProvider(BroadMarketTrendState nifty50Trend = BroadMarketTrendState.Neutral)
    {
        _context = new BroadMarketContext(
            new BroadMarketState(nifty50Trend, BroadMarketTrendState.Neutral, DateOnly.FromDateTime(DateTime.UtcNow)),
            Array.Empty<Candle>(),
            Array.Empty<Candle>());
    }

    public Task<BroadMarketContext> GetContextAsync(DateOnly asOfDate, CancellationToken ct = default) => Task.FromResult(_context);
}
