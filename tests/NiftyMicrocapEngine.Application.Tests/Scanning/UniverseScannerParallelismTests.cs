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

/// <summary>
/// Proves Stage 1's bounded-parallelism change (a) never exceeds the
/// configured max concurrent symbol count, and (b) still produces exactly
/// one result per symbol regardless of concurrency level — the two
/// properties that matter for the parallelization to be a safe optimization
/// rather than a correctness risk.
/// </summary>
public class UniverseScannerParallelismTests
{
    [Fact]
    public async Task RunAsync_NeverExceedsConfiguredMaxDegreeOfParallelism()
    {
        const int maxParallelism = 3;
        var tracker = new ConcurrencyTrackingRouter();

        var symbols = Enumerable.Range(1, 12).Select(i => new Symbol(i, "SYM" + i, "Company " + i, "IT", true)).ToList();

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<UniverseScanner>>();
        var snapshot = new UniverseSnapshot(1, new DateOnly(2026, 1, 15), DateTimeOffset.UtcNow);

        var scanner = new UniverseScanner(
            new FakeSymbolRepository(symbols),
            new FakeUniverseRepository(snapshot, symbols.Select(s => s.SymbolId).ToList()),
            new FakeCachingMarketDataService(tracker),
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
            Options.Create(new ScannerOptions { Stage2ShortlistSize = 5, MaxDegreeOfParallelism = maxParallelism }),
            Options.Create(new StructureThresholds()),
            logger);

        var result = await scanner.RunAsync(new DateOnly(2026, 1, 15));

        Assert.Equal(12, result.Stage1Results.Count); // every symbol still produced exactly one result
        Assert.True(tracker.MaxObservedConcurrency <= maxParallelism,
            $"Observed concurrency {tracker.MaxObservedConcurrency} exceeded configured max {maxParallelism}.");
    }

    private sealed class ConcurrencyTrackingRouter : NiftyMicrocapEngine.Application.DataAccess.IMarketDataRouter
    {
        private int _currentConcurrency;
        public int MaxObservedConcurrency { get; private set; }
        private readonly object _lock = new();

        public async Task<NiftyMicrocapEngine.Application.DataAccess.MarketDataFetchResult> GetCandlesAsync(
            int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _currentConcurrency++;
                MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, _currentConcurrency);
            }

            // Small delay so overlapping calls actually have a chance to
            // overlap in wall-clock time rather than completing too fast to
            // ever observe concurrency > 1.
            await Task.Delay(10, ct);

            var candles = new List<Candle>();
            var price = 100m;
            for (var d = 0; d < 60; d++)
            {
                var close = price + (decimal)(new Random(symbolId).NextDouble() - 0.5);
                candles.Add(new Candle(symbolId, timeframe, from.AddDays(d), price, close + 1, close - 1, close, close, 10000));
                price = close;
            }

            lock (_lock) { _currentConcurrency--; }

            return new NiftyMicrocapEngine.Application.DataAccess.MarketDataFetchResult(candles, Array.Empty<DataQualityFlag>());
        }
    }
}
