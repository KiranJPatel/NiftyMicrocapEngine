using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.DataQuality;
using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.MultiTimeframe;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Application.Scanning;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Scanning;

/// <summary>
/// Proves a single symbol's exception during Stage 1 or Stage 2 does not
/// crash the whole scan run — the specific resilience property added when
/// production-hardening the Scanner.
/// </summary>
public class UniverseScannerResilienceTests
{
    [Fact]
    public async Task RunAsync_OneSymbolThrowsDuringDataFetch_OthersStillScanned()
    {
        var symbols = new List<Symbol>
        {
            new(1, "GOODCO", "Good Co", "IT", true),
            new(2, "BADCO", "Bad Co", "IT", true),
            new(3, "OKCO", "OK Co", "IT", true)
        };

        var router = new ThrowingRouter(throwForSymbolId: 2, trendBySymbol: new Dictionary<int, string> { [1] = "up", [3] = "up" });

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UniverseScanner>>();
        var snapshot = new UniverseSnapshot(1, new DateOnly(2026, 1, 15), DateTimeOffset.UtcNow);

        var scanner = new UniverseScanner(
            new FakeSymbolRepository(symbols),
            new FakeUniverseRepository(snapshot, new List<int> { 1, 2, 3 }),
            new FakeCachingMarketDataService(router),
            new FakeBroadMarketContextProvider(),
            new DataQualityGate(Options.Create(new DataQualityGateOptions { TrailingWindowDays = 60, MinimumNonZeroVolumeDays = 1, MaxConsecutiveNoTradeDays = 60 })),
            new CircuitBandTracker(),
            new FakeNseCircuitBandProvider(),
            new MultiTimeframeEngine(Options.Create(new MultiTimeframeOptions())),
            new RegimeFilter(Options.Create(new DecisionEngineOptions())),
            new RelativeStrengthCalculator(Options.Create(new RelativeStrengthOptions())),
            new DecisionEngine(Options.Create(new DecisionEngineOptions())),
            new TradePlanBuilder(Options.Create(new RiskManagerOptions { StopAtrMultiple = 1.5m })),
            new CandlePsychologyAnalyzer(),
            new FakeIndicatorValueRepository(),
            new FakeMarketStructureEventRepository(),
            new FakeScanHistoryRepository(),
            Options.Create(new ScannerOptions { Stage2ShortlistSize = 5 }),
            Options.Create(new StructureThresholds()),
            logger);

        // The critical assertion: this must NOT throw, despite symbol 2 always throwing.
        var result = await scanner.RunAsync(new DateOnly(2026, 1, 15));

        Assert.Equal(3, result.Stage1Results.Count);
        Assert.Contains(result.Stage1Results, r => r.SymbolId == 2 && r.ExcludedByDataQualityGate);
        Assert.Contains(result.Stage1Results, r => r.SymbolId == 1 && !r.ExcludedByDataQualityGate);
        Assert.Contains(result.Stage1Results, r => r.SymbolId == 3 && !r.ExcludedByDataQualityGate);
    }

    private sealed class ThrowingRouter : IMarketDataRouter
    {
        private readonly int _throwForSymbolId;
        private readonly FakeMarketDataRouter _inner;

        public ThrowingRouter(int throwForSymbolId, Dictionary<int, string> trendBySymbol)
        {
            _throwForSymbolId = throwForSymbolId;
            _inner = new FakeMarketDataRouter(trendBySymbol);
        }

        public Task<MarketDataFetchResult> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            if (symbolId == _throwForSymbolId)
                throw new InvalidOperationException("Simulated provider failure for symbol " + symbolId);

            return _inner.GetCandlesAsync(symbolId, timeframe, from, to, ct);
        }
    }
}
