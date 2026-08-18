using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataQuality;
using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.MultiTimeframe;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Application.Scanning;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Scanning;

public class UniverseScannerTests
{
    private static UniverseScanner BuildScanner(Dictionary<int, string> trendBySymbol, IEnumerable<Symbol> symbols, int shortlistSize)
    {
        var symbolList = symbols.ToList();
        var symbolIds = symbolList.Select(s => s.SymbolId).ToList();

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UniverseScanner>>();

        var snapshot = new UniverseSnapshot(1, new DateOnly(2026, 1, 15), DateTimeOffset.UtcNow);

        return new UniverseScanner(
            new FakeSymbolRepository(symbolList),
            new FakeUniverseRepository(snapshot, symbolIds),
            new FakeCachingMarketDataService(new FakeMarketDataRouter(trendBySymbol)),
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
            Options.Create(new ScannerOptions { Stage2ShortlistSize = shortlistSize }),
            Options.Create(new StructureThresholds()),
            logger);
    }

    [Fact]
    public async Task RunAsync_CompletesWithoutThrowing_AndProducesStage1AndStage2Results()
    {
        var symbols = new List<Symbol>
        {
            new(1, "UPCO", "Up Company", "IT", true),
            new(2, "DOWNCO", "Down Company", "Pharma", true),
            new(3, "FLATCO", "Flat Company", "Auto", true)
        };
        var trends = new Dictionary<int, string> { [1] = "up", [2] = "down", [3] = "flat" };

        var scanner = BuildScanner(trends, symbols, 5);

        var result = await scanner.RunAsync(new DateOnly(2026, 1, 15));

        Assert.Equal(3, result.Stage1SymbolsScanned);
        Assert.NotNull(result.Stage1Results);
        Assert.NotNull(result.Stage2Results);
    }

    [Fact]
    public async Task RunAsync_Stage2ShortlistRespectsConfiguredSize()
    {
        var symbols = Enumerable.Range(1, 10).Select(i => new Symbol(i, "SYM" + i, "Company " + i, "IT", true)).ToList();
        var trends = symbols.ToDictionary(s => s.SymbolId, s => "up");

        var scanner = BuildScanner(trends, symbols, 3);

        var result = await scanner.RunAsync(new DateOnly(2026, 1, 15));

        Assert.True(result.Stage2Results.Count <= 3);
    }

    [Fact]
    public async Task RunAsync_Stage2Results_AreRankedByConfidenceDescending()
    {
        var symbols = new List<Symbol>
        {
            new(1, "UPCO", "Up Company", "IT", true),
            new(2, "DOWNCO", "Down Company", "Pharma", true)
        };
        var trends = new Dictionary<int, string> { [1] = "up", [2] = "down" };

        var scanner = BuildScanner(trends, symbols, 5);

        var result = await scanner.RunAsync(new DateOnly(2026, 1, 15));

        var scoredResults = result.Stage2Results.Where(r => r.DecisionResult is not null).ToList();
        for (var i = 1; i < scoredResults.Count; i++)
        {
            Assert.True(scoredResults[i - 1].DecisionResult!.ConfidenceScore >= scoredResults[i].DecisionResult!.ConfidenceScore);
        }
    }

    [Fact]
    public async Task RunAsync_NoUniverseSnapshot_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UniverseScanner>>();

        var scanner = new UniverseScanner(
            new FakeSymbolRepository(Array.Empty<Symbol>()),
            new NoSnapshotUniverseRepository(),
            new FakeCachingMarketDataService(new FakeMarketDataRouter(new Dictionary<int, string>())),
            new FakeBroadMarketContextProvider(),
            new DataQualityGate(Options.Create(new DataQualityGateOptions())),
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
            Options.Create(new ScannerOptions()),
            Options.Create(new StructureThresholds()),
            logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.RunAsync(new DateOnly(2026, 1, 15)));
    }

    [Fact]
    public async Task RunAsync_PersistsIndicatorSnapshotsForEachScannedSymbol()
    {
        var symbols = new List<Symbol> { new(1, "UPCO", "Up Company", "IT", true) };
        var trends = new Dictionary<int, string> { [1] = "up" };

        var indicatorRepo = new FakeIndicatorValueRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UniverseScanner>>();
        var snapshot = new UniverseSnapshot(1, new DateOnly(2026, 1, 15), DateTimeOffset.UtcNow);

        var scanner = new UniverseScanner(
            new FakeSymbolRepository(symbols),
            new FakeUniverseRepository(snapshot, new List<int> { 1 }),
            new FakeCachingMarketDataService(new FakeMarketDataRouter(trends)),
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
            indicatorRepo,
            new FakeMarketStructureEventRepository(),
            new FakeScanHistoryRepository(),
            Options.Create(new ScannerOptions { Stage2ShortlistSize = 5 }),
            Options.Create(new StructureThresholds()),
            logger);

        await scanner.RunAsync(new DateOnly(2026, 1, 15));

        Assert.NotEmpty(indicatorRepo.Saved);
        Assert.Contains(indicatorRepo.Saved, s => s.SymbolId == 1 && s.IndicatorKey.StartsWith("ATR"));
    }
}
