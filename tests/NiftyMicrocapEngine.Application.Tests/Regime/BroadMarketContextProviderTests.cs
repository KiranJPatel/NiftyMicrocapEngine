using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Regime;

public class BroadMarketContextProviderTests
{
    private static BroadMarketContextProvider BuildProvider(FakeIndexProvider provider) =>
        new(new IMarketDataProvider[] { provider },
            Options.Create(new BenchmarkIndicesOptions()),
            Options.Create(new StructureThresholds()),
            NullLogger<BroadMarketContextProvider>.Instance);

    [Fact]
    public async Task GetContextAsync_NoYahooProviderRegistered_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new BroadMarketContextProvider(Array.Empty<IMarketDataProvider>(),
                Options.Create(new BenchmarkIndicesOptions()),
                Options.Create(new StructureThresholds()),
                NullLogger<BroadMarketContextProvider>.Instance));
    }

    [Fact]
    public async Task GetContextAsync_UptrendingIndex_ClassifiesAsBullOrStrongBull()
    {
        var candles = GenerateTrendingSeries(direction: 1, count: 60);
        var provider = new FakeIndexProvider(candles);
        var contextProvider = BuildProvider(provider);

        var context = await contextProvider.GetContextAsync(new DateOnly(2026, 3, 1));

        Assert.True(context.RegimeState.Nifty50Trend is BroadMarketTrendState.Bull or BroadMarketTrendState.StrongBull);
    }

    [Fact]
    public async Task GetContextAsync_DowntrendingIndex_ClassifiesAsBearOrStrongBear()
    {
        var candles = GenerateTrendingSeries(direction: -1, count: 60);
        var provider = new FakeIndexProvider(candles);
        var contextProvider = BuildProvider(provider);

        var context = await contextProvider.GetContextAsync(new DateOnly(2026, 3, 1));

        Assert.True(context.RegimeState.Nifty50Trend is BroadMarketTrendState.Bear or BroadMarketTrendState.StrongBear);
    }

    [Fact]
    public async Task GetContextAsync_FetchThrows_DegradesToNeutralRatherThanPropagating()
    {
        var provider = new FakeIndexProvider(throwOnFetch: true);
        var contextProvider = BuildProvider(provider);

        var context = await contextProvider.GetContextAsync(new DateOnly(2026, 3, 1));

        Assert.Equal(BroadMarketTrendState.Neutral, context.RegimeState.Nifty50Trend);
        Assert.Empty(context.Nifty50Candles);
    }

    [Fact]
    public async Task GetContextAsync_ReturnsAllThreeCandleSeries()
    {
        var candles = GenerateTrendingSeries(direction: 1, count: 60);
        var provider = new FakeIndexProvider(candles);
        var contextProvider = BuildProvider(provider);

        var context = await contextProvider.GetContextAsync(new DateOnly(2026, 3, 1));

        Assert.NotEmpty(context.Nifty50Candles);
        Assert.NotEmpty(context.NiftyMicrocap250Candles);
    }

    private static List<Candle> GenerateTrendingSeries(int direction, int count)
    {
        var candles = new List<Candle>();
        var price = 100m;
        for (var i = 0; i < count; i++)
        {
            var close = price + direction * 2m;
            var high = Math.Max(price, close) + 0.5m;
            var low = Math.Min(price, close) - 0.5m;
            candles.Add(new Candle(0, Timeframe.Daily, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i), price, high, low, close, close, 10000));
            price = close;
        }
        return candles;
    }

    private sealed class FakeIndexProvider : IMarketDataProvider
    {
        private readonly List<Candle>? _candles;
        private readonly bool _throwOnFetch;

        public DataProviderKind ProviderKind => DataProviderKind.Yahoo;

        public FakeIndexProvider(List<Candle> candles) => _candles = candles;
        public FakeIndexProvider(bool throwOnFetch) => _throwOnFetch = throwOnFetch;

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string providerSymbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            if (_throwOnFetch) throw new InvalidOperationException("Simulated fetch failure.");
            return Task.FromResult<IReadOnlyList<Candle>>(_candles ?? new List<Candle>());
        }

        public Task<ProviderHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
            Task.FromResult(new ProviderHealthCheckResult(true, "fake"));
    }
}
