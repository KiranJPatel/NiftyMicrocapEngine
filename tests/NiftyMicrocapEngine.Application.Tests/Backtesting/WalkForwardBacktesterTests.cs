using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Backtesting;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataQuality;
using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.MultiTimeframe;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Application.Scanning;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Application.Tests.Scanning;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Backtesting;

/// <summary>
/// Reuses the exact fake/real-engine construction pattern from
/// UniverseScannerTests.BuildScanner — WalkForwardBacktester takes almost
/// the same dependency set (see its own doc comment on why it doesn't reuse
/// UniverseScanner directly), so the same fakes apply.
/// </summary>
public class WalkForwardBacktesterTests
{
    private static WalkForwardBacktester BuildBacktester(Dictionary<int, string> trendBySymbol, IEnumerable<Symbol> symbols)
    {
        var symbolList = symbols.ToList();

        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WalkForwardBacktester>>();

        var snapshot = new UniverseSnapshot(1, new DateOnly(2024, 1, 1), DateTimeOffset.UtcNow);

        return new WalkForwardBacktester(
            new FakeSymbolRepository(symbolList),
            new FakeUniverseRepository(snapshot, symbolList.Select(s => s.SymbolId).ToList()),
            new FakeCachingMarketDataService(new FakeMarketDataRouter(trendBySymbol)),
            new FakeBroadMarketContextProvider(),
            new DataQualityGate(Options.Create(new DataQualityGateOptions { TrailingWindowDays = 60, MinimumNonZeroVolumeDays = 1, MaxConsecutiveNoTradeDays = 60 })),
            new CircuitBandTracker(),
            new MultiTimeframeEngine(Options.Create(new MultiTimeframeOptions())),
            new RegimeFilter(Options.Create(new DecisionEngineOptions())),
            new RelativeStrengthCalculator(Options.Create(new RelativeStrengthOptions())),
            new DecisionEngine(Options.Create(new DecisionEngineOptions())),
            new TradePlanBuilder(Options.Create(new RiskManagerOptions { StopAtrMultiple = 1.5m })),
            new CandlePsychologyAnalyzer(),
            Options.Create(new StructureThresholds()),
            logger);
    }

    [Fact]
    public async Task RunAsync_CompletesWithoutThrowing_AndProducesAReport()
    {
        var symbols = new List<Symbol> { new(1, "UPCO", "Up Company", "IT", true) };
        var trends = new Dictionary<int, string> { [1] = "up" };
        var backtester = BuildBacktester(trends, symbols);

        var request = new BacktestRequest(new DateOnly(2024, 1, 1), new DateOnly(2024, 7, 1), CadenceTradingDays: 10, SymbolIds: new[] { 1 });
        var report = await backtester.RunAsync(request);

        Assert.Equal(1, report.SymbolsWalked);
        Assert.True(report.TotalAsOfDatesEvaluated > 0);
        Assert.NotNull(report.BucketStats);
        Assert.Equal(2, report.BucketStats.Count); // StrongBuy and Buy buckets, always both present even if empty
    }

    [Fact]
    public async Task RunAsync_NoRepaint_ExtendingTheEndDateDoesNotChangeEarlierSignals()
    {
        // The core no-repaint property this whole engine depends on (§21):
        // a decision made as of date X must never change just because more
        // data became available after X. FakeMarketDataRouter generates a
        // fully deterministic series from a fixed Random(symbolId) seed
        // starting at the SAME `from` date every call, so as long as both
        // runs share the same StartDate (and therefore the same fetchFrom),
        // run B's candle series is an exact prefix-extension of run A's —
        // any difference in run B's earlier signals versus run A's would be
        // a genuine repaint bug in WalkForwardBacktester's slicing, not an
        // artifact of the fake.
        var symbols = new List<Symbol> { new(1, "UPCO", "Up Company", "IT", true) };
        var trends = new Dictionary<int, string> { [1] = "up" };

        var startDate = new DateOnly(2024, 1, 1);
        var shorterEndDate = new DateOnly(2024, 7, 1);
        var longerEndDate = new DateOnly(2024, 12, 1); // five extra months of future data run B can see that run A cannot

        var backtesterA = BuildBacktester(trends, symbols);
        var reportA = await backtesterA.RunAsync(new BacktestRequest(startDate, shorterEndDate, CadenceTradingDays: 10, SymbolIds: new[] { 1 }));

        var backtesterB = BuildBacktester(trends, symbols); // fresh instance — no shared mutable state that could leak between runs
        var reportB = await backtesterB.RunAsync(new BacktestRequest(startDate, longerEndDate, CadenceTradingDays: 10, SymbolIds: new[] { 1 }));

        // Every trade signal A generated (as-of dates <= shorterEndDate by
        // construction) must appear in B's trade list with IDENTICAL
        // decision, confidence, and trade-plan levels — not just "a similar
        // trade around that date."
        Assert.NotEmpty(reportA.Trades); // if this trips, the "up" trend fixture needs adjusting to actually cross the Buy threshold — not a backtester bug, but the test can't prove anything without at least one signal
        foreach (var tradeA in reportA.Trades)
        {
            var matchInB = reportB.Trades.SingleOrDefault(t => t.AsOfDate == tradeA.AsOfDate && t.SymbolId == tradeA.SymbolId);
            Assert.True(matchInB is not null, $"Trade at {tradeA.AsOfDate} present in the shorter run vanished in the longer run — a repaint.");
            Assert.Equal(tradeA.Decision, matchInB!.Decision);
            Assert.Equal(tradeA.ConfidenceScore, matchInB.ConfidenceScore);
            Assert.Equal(tradeA.Plan.Entry, matchInB.Plan.Entry);
            Assert.Equal(tradeA.Plan.StopLoss, matchInB.Plan.StopLoss);
            Assert.Equal(tradeA.Plan.Target1, matchInB.Plan.Target1);
        }
    }
}
