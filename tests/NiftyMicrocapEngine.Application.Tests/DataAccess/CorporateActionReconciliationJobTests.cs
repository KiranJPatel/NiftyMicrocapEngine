using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.DataAccess;

public class CorporateActionReconciliationJobTests
{
    private static Candle Candle(int symbolId, DateTimeOffset date, decimal adjClose) => new(
        symbolId, Timeframe.Daily, date, 100m, 105m, 99m, 102m, adjClose, 10000);

    private static CorporateActionReconciliationJob BuildJob(
        FakeSymbolRepo symbolRepo, FakeCandleRepo candleRepo, FakeProvider provider, decimal tolerance = 0.001m)
    {
        var options = new ReconciliationOptions { LookbackDays = 90, AdjCloseToleranceFraction = tolerance };
        return new CorporateActionReconciliationJob(symbolRepo, candleRepo, new IMarketDataProvider[] { provider }, Options.Create(options), NullLogger<CorporateActionReconciliationJob>.Instance);
    }

    [Fact]
    public async Task RunAsync_AdjCloseDivergesBeyondTolerance_OverwritesAndReports()
    {
        var date = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var symbolRepo = new FakeSymbolRepo(new[] { new Symbol(1, "RELIANCE", "Reliance", "Energy", true) });
        var candleRepo = new FakeCandleRepo();
        candleRepo.Seed(new[] { Candle(1, date, 100m) }); // cached AdjClose 100

        var provider = new FakeProvider(new[] { Candle(0, date, 95m) }); // fresh AdjClose 95 -> 5% divergence, well beyond 0.1%

        var job = BuildJob(symbolRepo, candleRepo, provider);
        var result = await job.RunAsync();

        Assert.Single(result.Overwrites);
        Assert.Equal(100m, result.Overwrites[0].OldAdjClose);
        Assert.Equal(95m, result.Overwrites[0].NewAdjClose);
    }

    [Fact]
    public async Task RunAsync_AdjCloseWithinTolerance_DoesNotOverwrite()
    {
        var date = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var symbolRepo = new FakeSymbolRepo(new[] { new Symbol(1, "RELIANCE", "Reliance", "Energy", true) });
        var candleRepo = new FakeCandleRepo();
        candleRepo.Seed(new[] { Candle(1, date, 100m) });

        var provider = new FakeProvider(new[] { Candle(0, date, 100.05m) }); // 0.05% divergence, under 0.1% tolerance

        var job = BuildJob(symbolRepo, candleRepo, provider);
        var result = await job.RunAsync();

        Assert.Empty(result.Overwrites);
    }

    [Fact]
    public async Task RunAsync_OneSymbolFails_OthersStillProcessed()
    {
        var date = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var symbolRepo = new FakeSymbolRepo(new[]
        {
            new Symbol(1, "FAILCO", "Fail Co", "IT", true),
            new Symbol(2, "OKCO", "OK Co", "IT", true)
        });
        var candleRepo = new FakeCandleRepo();
        candleRepo.Seed(new[] { Candle(1, date, 100m), Candle(2, date, 100m) });

        var provider = new FakeProvider(new[] { Candle(0, date, 90m) }, throwForSymbol: "FAILCO");

        var job = BuildJob(symbolRepo, candleRepo, provider);
        var result = await job.RunAsync();

        Assert.Equal(1, result.SymbolsFailed);
        Assert.Single(result.Overwrites); // OKCO's overwrite still applied despite FAILCO throwing
    }

    [Fact]
    public async Task RunAsync_NewDateNotPreviouslyCached_BackfillsWithoutReportingAsOverwrite()
    {
        var date = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var symbolRepo = new FakeSymbolRepo(new[] { new Symbol(1, "RELIANCE", "Reliance", "Energy", true) });
        var candleRepo = new FakeCandleRepo(); // nothing cached

        var provider = new FakeProvider(new[] { Candle(0, date, 100m) });

        var job = BuildJob(symbolRepo, candleRepo, provider);
        var result = await job.RunAsync();

        Assert.Empty(result.Overwrites); // backfill, not a correction
        Assert.NotEmpty(candleRepo.SavedCandles); // but it was saved
    }

    private sealed class FakeSymbolRepo : ISymbolRepository
    {
        private readonly List<Symbol> _symbols;
        public FakeSymbolRepo(IEnumerable<Symbol> symbols) => _symbols = symbols.ToList();

        public Task<Symbol?> GetBySymbolIdAsync(int symbolId, CancellationToken ct = default) => Task.FromResult(_symbols.FirstOrDefault(s => s.SymbolId == symbolId));
        public Task<Symbol?> GetByNseSymbolAsync(string nseSymbol, CancellationToken ct = default) => Task.FromResult(_symbols.FirstOrDefault(s => s.NseSymbol == nseSymbol));
        public Task<IReadOnlyList<Symbol>> GetAllActiveAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Symbol>>(_symbols.Where(s => s.IsActive).ToList());
        public Task<int> UpsertAsync(Symbol symbol, CancellationToken ct = default) => Task.FromResult(symbol.SymbolId);
        public Task SaveMappingAsync(SymbolMapping mapping, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SymbolMapping?> GetActiveMappingAsync(int symbolId, DataProviderKind provider, DateOnly asOf, CancellationToken ct = default) => Task.FromResult<SymbolMapping?>(null);
    }

    private sealed class FakeCandleRepo : ICandleRepository
    {
        private readonly List<Candle> _store = new();
        public List<Candle> SavedCandles => _store;
        public void Seed(IEnumerable<Candle> candles) => _store.AddRange(candles);

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Candle>>(_store.Where(c => c.SymbolId == symbolId && c.Timestamp >= from && c.Timestamp <= to).ToList());

        public Task SaveCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default)
        {
            foreach (var c in candles)
            {
                _store.RemoveAll(x => x.SymbolId == c.SymbolId && x.Timestamp == c.Timestamp);
                _store.Add(c);
            }
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset?> GetLatestTimestampAsync(int symbolId, Timeframe timeframe, CancellationToken ct = default) =>
            Task.FromResult(_store.Where(c => c.SymbolId == symbolId).Select(c => (DateTimeOffset?)c.Timestamp).OrderByDescending(d => d).FirstOrDefault());
    }

    private sealed class FakeProvider : IMarketDataProvider
    {
        private readonly Candle[] _candles;
        private readonly string? _throwForSymbol;
        public DataProviderKind ProviderKind => DataProviderKind.Yahoo;

        public FakeProvider(Candle[] candles, string? throwForSymbol = null)
        {
            _candles = candles;
            _throwForSymbol = throwForSymbol;
        }

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string providerSymbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            if (_throwForSymbol is not null && providerSymbol.StartsWith(_throwForSymbol, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Simulated provider failure for " + providerSymbol);

            return Task.FromResult<IReadOnlyList<Candle>>(_candles);
        }

        public Task<ProviderHealthCheckResult> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(new ProviderHealthCheckResult(true, "fake"));
    }
}
