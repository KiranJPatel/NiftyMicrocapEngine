using Microsoft.Extensions.Logging.Abstractions;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;
using NiftyMicrocapEngine.Infrastructure.BrokerData;
using Xunit;

namespace NiftyMicrocapEngine.Infrastructure.Tests.BrokerData;

public class FallbackMarketDataRouterTests
{
    private static Candle Candle(int symbolId, int day, DataProviderKind provider = DataProviderKind.Yahoo, Timeframe tf = Timeframe.Daily) => new(
        symbolId, tf, new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), 100m, 105m, 99m, 102m, 102m, 10000);

    private static FakeSymbolRepository DefaultSymbolRepo() => new(new Symbol(1, "RELIANCE", "Reliance Industries", "Energy", true));

    [Fact]
    public async Task GetCandlesAsync_WhenPrimarySucceedsWithFullData_UsesPrimaryOnly()
    {
        var primaryCandles = Enumerable.Range(1, 20).Select(d => Candle(0, d)).ToList(); // SymbolId=0 sentinel, as real providers return
        var primary = new FakeProvider(DataProviderKind.Yahoo, primaryCandles);
        var secondary = new FakeProvider(DataProviderKind.Broker, Array.Empty<Candle>());

        var router = new FallbackMarketDataRouter(new IMarketDataProvider[] { primary, secondary }, DefaultSymbolRepo(), NullLogger<FallbackMarketDataRouter>.Instance);

        var result = await router.GetCandlesAsync(1, Timeframe.Daily, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(20, result.Candles.Count);
        Assert.All(result.Candles, c => Assert.Equal(1, c.SymbolId)); // router stamps the real SymbolId
        Assert.False(secondary.WasCalled);
        Assert.Empty(result.QualityFlags);
    }

    [Fact]
    public async Task GetCandlesAsync_WhenPrimaryThrows_FallsBackToSecondaryAndFlags()
    {
        var primary = new FakeProvider(DataProviderKind.Yahoo, throwOnFetch: true);
        var secondary = new FakeProvider(DataProviderKind.Broker, new List<Candle> { Candle(0, 1) });

        var router = new FallbackMarketDataRouter(new IMarketDataProvider[] { primary, secondary }, DefaultSymbolRepo(), NullLogger<FallbackMarketDataRouter>.Instance);

        var result = await router.GetCandlesAsync(1, Timeframe.Daily, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(secondary.WasCalled);
        Assert.Single(result.Candles);
        Assert.Contains(result.QualityFlags, f => f.FlagType == "SecondaryProviderFallbackUsed");
    }

    [Fact]
    public async Task GetCandlesAsync_WhenPrimaryReturnsMaterialGap_FallsBackToSecondaryAndFlags()
    {
        var primaryCandles = new List<Candle> { Candle(0, 1), Candle(0, 2) };
        var secondaryCandles = Enumerable.Range(1, 20).Select(d => Candle(0, d)).ToList();

        var primary = new FakeProvider(DataProviderKind.Yahoo, primaryCandles);
        var secondary = new FakeProvider(DataProviderKind.Broker, secondaryCandles);

        var router = new FallbackMarketDataRouter(new IMarketDataProvider[] { primary, secondary }, DefaultSymbolRepo(), NullLogger<FallbackMarketDataRouter>.Instance);

        var result = await router.GetCandlesAsync(1, Timeframe.Daily, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 28, 0, 0, 0, TimeSpan.Zero));

        Assert.True(secondary.WasCalled);
        Assert.Contains(result.QualityFlags, f => f.FlagType == "SecondaryProviderFallbackUsed");
    }

    [Fact]
    public async Task GetCandlesAsync_ForConfirmationOnlyTimeframe_GoesStraightToSecondary()
    {
        var primary = new FakeProvider(DataProviderKind.Yahoo, Array.Empty<Candle>());
        var secondary = new FakeProvider(DataProviderKind.Broker, new List<Candle> { Candle(0, 1, tf: Timeframe.H1) });

        var router = new FallbackMarketDataRouter(new IMarketDataProvider[] { primary, secondary }, DefaultSymbolRepo(), NullLogger<FallbackMarketDataRouter>.Instance);

        var result = await router.GetCandlesAsync(1, Timeframe.H1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.False(primary.WasCalled);
        Assert.True(secondary.WasCalled);
        Assert.Single(result.Candles);
    }

    [Fact]
    public void Constructor_WithoutPrimaryProvider_Throws()
    {
        var secondary = new FakeProvider(DataProviderKind.Broker, Array.Empty<Candle>());
        Assert.Throws<InvalidOperationException>(() =>
            new FallbackMarketDataRouter(new IMarketDataProvider[] { secondary }, DefaultSymbolRepo(), NullLogger<FallbackMarketDataRouter>.Instance));
    }

    [Fact]
    public void Constructor_WithoutSecondaryProvider_Throws()
    {
        var primary = new FakeProvider(DataProviderKind.Yahoo, Array.Empty<Candle>());
        Assert.Throws<InvalidOperationException>(() =>
            new FallbackMarketDataRouter(new IMarketDataProvider[] { primary }, DefaultSymbolRepo(), NullLogger<FallbackMarketDataRouter>.Instance));
    }

    private sealed class FakeProvider : IMarketDataProvider
    {
        private readonly IReadOnlyList<Candle>? _candlesToReturn;
        private readonly bool _throwOnFetch;

        public bool WasCalled { get; private set; }
        public DataProviderKind ProviderKind { get; }

        public FakeProvider(DataProviderKind kind, IReadOnlyList<Candle> candlesToReturn)
        {
            ProviderKind = kind;
            _candlesToReturn = candlesToReturn;
        }

        public FakeProvider(DataProviderKind kind, bool throwOnFetch)
        {
            ProviderKind = kind;
            _throwOnFetch = throwOnFetch;
        }

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string providerSymbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            WasCalled = true;
            if (_throwOnFetch) throw new InvalidOperationException("Simulated provider failure.");
            return Task.FromResult(_candlesToReturn!);
        }

        public Task<ProviderHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) =>
            Task.FromResult(new ProviderHealthCheckResult(true, "fake"));
    }

    private sealed class FakeSymbolRepository : ISymbolRepository
    {
        private readonly Symbol _symbol;

        public FakeSymbolRepository(Symbol symbol) => _symbol = symbol;

        public Task<Symbol?> GetBySymbolIdAsync(int symbolId, CancellationToken ct = default) =>
            Task.FromResult<Symbol?>(symbolId == _symbol.SymbolId ? _symbol : null);

        public Task<Symbol?> GetByNseSymbolAsync(string nseSymbol, CancellationToken ct = default) =>
            Task.FromResult<Symbol?>(nseSymbol == _symbol.NseSymbol ? _symbol : null);

        public Task<IReadOnlyList<Symbol>> GetAllActiveAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>(new[] { _symbol });

        public Task<int> UpsertAsync(Symbol symbol, CancellationToken ct = default) => Task.FromResult(symbol.SymbolId);

        public Task SaveMappingAsync(SymbolMapping mapping, CancellationToken ct = default) => Task.CompletedTask;

        // Yahoo intentionally has no explicit mapping — forces the router's
        // fallback-to-NSE-symbol-suffix path. Broker DOES need an explicit mapping
        // (per ZerodhaMarketDataProvider's instrument-token requirement), so provide
        // one here — without it, every fallback-to-secondary test would fail for a
        // reason unrelated to what it's actually testing.
        public Task<SymbolMapping?> GetActiveMappingAsync(int symbolId, DataProviderKind provider, DateOnly asOf, CancellationToken ct = default)
        {
            if (provider == DataProviderKind.Broker && symbolId == _symbol.SymbolId)
            {
                return Task.FromResult<SymbolMapping?>(new SymbolMapping(symbolId, provider, "738561", new DateOnly(2020, 1, 1), null));
            }
            return Task.FromResult<SymbolMapping?>(null);
        }
    }
}
