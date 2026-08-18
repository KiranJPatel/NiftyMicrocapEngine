using Microsoft.Extensions.Logging.Abstractions;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.DataAccess;

public class CachingMarketDataServiceTests
{
    private static Candle Candle(int symbolId, int day, Timeframe tf = Timeframe.Daily) => new(
        symbolId, tf, new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), 100m, 105m, 99m, 102m, 102m, 10000);

    [Fact]
    public async Task GetCandlesAsync_NoCacheAtAll_FetchesFullRangeAndPersists()
    {
        var repo = new FakeCandleRepository();
        var router = new FakeRouter(new List<Candle> { Candle(1, 1), Candle(1, 2), Candle(1, 3) });
        var service = new CachingMarketDataService(repo, router, NullLogger<CachingMarketDataService>.Instance);

        var result = await service.GetCandlesAsync(1, Timeframe.Daily,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(3, result.Count);
        Assert.Equal(1, router.CallCount);
        Assert.True(repo.SavedCandles.Count > 0);
    }

    [Fact]
    public async Task GetCandlesAsync_CacheFullyCoversRange_DoesNotCallRouter()
    {
        var repo = new FakeCandleRepository();
        repo.Seed(new List<Candle> { Candle(1, 1), Candle(1, 2), Candle(1, 3) });

        var router = new FakeRouter(new List<Candle>());
        var service = new CachingMarketDataService(repo, router, NullLogger<CachingMarketDataService>.Instance);

        var result = await service.GetCandlesAsync(1, Timeframe.Daily,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(3, result.Count);
        Assert.Equal(0, router.CallCount);
    }

    [Fact]
    public async Task GetCandlesAsync_CachePartiallyCovers_FetchesOnlyDelta()
    {
        var repo = new FakeCandleRepository();
        repo.Seed(new List<Candle> { Candle(1, 1), Candle(1, 2) }); // cached through day 2

        var router = new FakeRouter(new List<Candle> { Candle(1, 2), Candle(1, 3), Candle(1, 4) }); // router serves the delta
        var service = new CachingMarketDataService(repo, router, NullLogger<CachingMarketDataService>.Instance);

        var result = await service.GetCandlesAsync(1, Timeframe.Daily,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(1, router.CallCount);
        // Router should have been asked starting from day 1 (day2 - 1 day step), not day 1 of the original range redundantly re-fetched in full.
        Assert.True(router.LastFrom < new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(4, result.Count); // merged cache + delta, served back from repo
    }

    [Fact]
    public async Task GetCandlesAsync_EmptyFetchResult_DoesNotThrowOnSave()
    {
        var repo = new FakeCandleRepository();
        var router = new FakeRouter(new List<Candle>());
        var service = new CachingMarketDataService(repo, router, NullLogger<CachingMarketDataService>.Instance);

        var result = await service.GetCandlesAsync(1, Timeframe.Daily,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(result);
    }

    private sealed class FakeCandleRepository : ICandleRepository
    {
        private readonly List<Candle> _store = new();
        public List<Candle> SavedCandles => _store;

        public void Seed(IEnumerable<Candle> candles) => _store.AddRange(candles);

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Candle>>(_store.Where(c => c.SymbolId == symbolId && c.Timeframe == timeframe && c.Timestamp >= from && c.Timestamp <= to)
                .OrderBy(c => c.Timestamp).ToList());

        public Task SaveCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default)
        {
            foreach (var candle in candles)
            {
                _store.RemoveAll(c => c.SymbolId == candle.SymbolId && c.Timeframe == candle.Timeframe && c.Timestamp == candle.Timestamp);
                _store.Add(candle);
            }
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetLatestTimestampAsync(int symbolId, Timeframe timeframe, CancellationToken ct = default)
        {
            var matches = _store.Where(c => c.SymbolId == symbolId && c.Timeframe == timeframe).ToList();
            return Task.FromResult(matches.Count == 0 ? (DateTimeOffset?)null : matches.Max(c => c.Timestamp));
        }
    }

    private sealed class FakeRouter : IMarketDataRouter
    {
        private readonly List<Candle> _toReturn;
        public int CallCount { get; private set; }
        public DateTimeOffset LastFrom { get; private set; }

        public FakeRouter(List<Candle> toReturn) => _toReturn = toReturn;

        public Task<MarketDataFetchResult> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            CallCount++;
            LastFrom = from;
            return Task.FromResult(new MarketDataFetchResult(_toReturn, Array.Empty<DataQualityFlag>()));
        }
    }
}
