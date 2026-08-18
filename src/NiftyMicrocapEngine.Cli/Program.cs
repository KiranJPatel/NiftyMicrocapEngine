using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Infrastructure.BrokerData;
using NiftyMicrocapEngine.Infrastructure.Persistence;
using NiftyMicrocapEngine.Infrastructure.YahooFinance;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables();

// Config binding — section names match build spec §19 exactly.
builder.Services.Configure<DataProvidersOptions>(builder.Configuration.GetSection(DataProvidersOptions.SectionName));
builder.Services.Configure<NseIndicesOptions>(builder.Configuration.GetSection(NseIndicesOptions.SectionName));
builder.Services.Configure<BenchmarkIndicesOptions>(builder.Configuration.GetSection(BenchmarkIndicesOptions.SectionName));
builder.Services.Configure<DataProviderRoutingOptions>(builder.Configuration.GetSection(DataProviderRoutingOptions.SectionName));
builder.Services.Configure<ReconciliationOptions>(builder.Configuration.GetSection(ReconciliationOptions.SectionName));
builder.Services.Configure<DataQualityGateOptions>(builder.Configuration.GetSection(DataQualityGateOptions.SectionName));
builder.Services.Configure<StructureThresholds>(builder.Configuration.GetSection(StructureThresholds.SectionName));
builder.Services.Configure<MultiTimeframeOptions>(builder.Configuration.GetSection(MultiTimeframeOptions.SectionName));
builder.Services.Configure<DecisionEngineOptions>(builder.Configuration.GetSection(DecisionEngineOptions.SectionName));
builder.Services.Configure<RelativeStrengthOptions>(builder.Configuration.GetSection(RelativeStrengthOptions.SectionName));
builder.Services.Configure<RiskManagerOptions>(builder.Configuration.GetSection(RiskManagerOptions.SectionName));
builder.Services.Configure<ScannerOptions>(builder.Configuration.GetSection(ScannerOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

// Fail fast on a misconfigured appsettings.json rather than produce silently
// wrong decisions (weights not summing to 100, a gate no symbol could ever
// pass, a negative risk parameter) at first use.
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<DecisionEngineOptions>, NiftyMicrocapEngine.Application.Configuration.Validation.DecisionEngineOptionsValidator>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<MultiTimeframeOptions>, NiftyMicrocapEngine.Application.Configuration.Validation.MultiTimeframeOptionsValidator>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<DataQualityGateOptions>, NiftyMicrocapEngine.Application.Configuration.Validation.DataQualityGateOptionsValidator>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<RiskManagerOptions>, NiftyMicrocapEngine.Application.Configuration.Validation.RiskManagerOptionsValidator>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ReconciliationOptions>, NiftyMicrocapEngine.Application.Configuration.Validation.ReconciliationOptionsValidator>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ScannerOptions>, NiftyMicrocapEngine.Application.Configuration.Validation.ScannerOptionsValidator>();
builder.Services.AddOptions<DecisionEngineOptions>().ValidateOnStart();
builder.Services.AddOptions<MultiTimeframeOptions>().ValidateOnStart();
builder.Services.AddOptions<DataQualityGateOptions>().ValidateOnStart();
builder.Services.AddOptions<RiskManagerOptions>().ValidateOnStart();
builder.Services.AddOptions<ReconciliationOptions>().ValidateOnStart();

var sqliteConnectionString = builder.Configuration["Storage:SqliteConnectionString"] ?? "Data Source=niftymicrocapengine.db";
var sqliteFilePath = sqliteConnectionString.Replace("Data Source=", "", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSqlitePersistence(sqliteFilePath);

builder.Services.AddYahooFinanceProvider();
builder.Services.AddBrokerDataProvider();

builder.Services.AddSingleton<NiftyMicrocapEngine.Application.MultiTimeframe.IMultiTimeframeEngine, NiftyMicrocapEngine.Application.MultiTimeframe.MultiTimeframeEngine>();
builder.Services.AddSingleton<NiftyMicrocapEngine.Application.Regime.IRegimeFilter, NiftyMicrocapEngine.Application.Regime.RegimeFilter>();
builder.Services.AddSingleton<NiftyMicrocapEngine.Application.Regime.IRelativeStrengthCalculator, NiftyMicrocapEngine.Application.Regime.RelativeStrengthCalculator>();
builder.Services.AddSingleton<NiftyMicrocapEngine.Application.Decision.IDecisionEngine, NiftyMicrocapEngine.Application.Decision.DecisionEngine>();
builder.Services.AddSingleton<NiftyMicrocapEngine.Application.Risk.ITradePlanBuilder, NiftyMicrocapEngine.Application.Risk.TradePlanBuilder>();
builder.Services.AddSingleton<NiftyMicrocapEngine.Application.Risk.IPortfolioRiskManager, NiftyMicrocapEngine.Application.Risk.PortfolioRiskManager>();
builder.Services.AddSingleton<NiftyMicrocapEngine.Application.DataQuality.IDataQualityGate, NiftyMicrocapEngine.Application.DataQuality.DataQualityGate>();
builder.Services.AddSingleton<NiftyMicrocapEngine.Application.DataQuality.ICircuitBandTracker, NiftyMicrocapEngine.Application.DataQuality.CircuitBandTracker>();
builder.Services.AddSingleton<NiftyMicrocapEngine.Application.Structure.ICandlePsychologyAnalyzer>(_ => new NiftyMicrocapEngine.Application.Structure.CandlePsychologyAnalyzer());
builder.Services.AddScoped<NiftyMicrocapEngine.Application.DataAccess.ICachingMarketDataService, NiftyMicrocapEngine.Application.DataAccess.CachingMarketDataService>();
builder.Services.AddSingleton<NiftyMicrocapEngine.Application.Regime.IBroadMarketContextProvider, NiftyMicrocapEngine.Application.Regime.BroadMarketContextProvider>();
builder.Services.AddScoped<NiftyMicrocapEngine.Application.DataAccess.ICorporateActionReconciliationJob, NiftyMicrocapEngine.Application.DataAccess.CorporateActionReconciliationJob>();
builder.Services.AddScoped<NiftyMicrocapEngine.Application.Scanning.IUniverseScanner, NiftyMicrocapEngine.Application.Scanning.UniverseScanner>();
builder.Services.AddScoped<NiftyMicrocapEngine.Application.Backtesting.IWalkForwardBacktester, NiftyMicrocapEngine.Application.Backtesting.WalkForwardBacktester>();

using var host = builder.Build();

// This CLI is a one-shot command dispatcher, not a long-running host — it
// never calls host.RunAsync()/StartAsync(), so ValidateOnStart's normal
// hosted-service trigger never fires. Force validation explicitly here
// instead, resolving each validated IOptions<T> once so its IValidateOptions
// runs before any command executes — fail fast on bad config rather than
// mid-scan.
ValidateStartupOptions(host.Services);

static void ValidateStartupOptions(IServiceProvider services)
{
    _ = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DecisionEngineOptions>>().Value;
    _ = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MultiTimeframeOptions>>().Value;
    _ = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DataQualityGateOptions>>().Value;
    _ = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RiskManagerOptions>>().Value;
    _ = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReconciliationOptions>>().Value;
}

var command = args.Length > 0 ? args[0] : "help";
var subCommand = args.Length > 1 ? args[1] : null;

return command switch
{
    "db" when subCommand == "migrate" => await RunDbMigrateAsync(host.Services),
    "health-check" => await RunHealthCheckAsync(host.Services),
    "universe" when subCommand == "fetch" => await RunUniverseFetchAsync(host.Services),
    "scan" => await RunScanAsync(host.Services, args),
    "reconcile" => await RunReconcileAsync(host.Services),
    "benchmark" => await RunBenchmarkAsync(host.Services, args),
    "backtest" => await RunBacktestAsync(host.Services, args),
    _ => PrintHelp()
};

static int PrintHelp()
{
    Console.WriteLine("""
        Nifty Microcap Engine — CLI

        Usage:
          dotnet run -- db migrate         Apply pending SQLite schema migrations.
          dotnet run -- health-check       Check connectivity to Yahoo, NSE Indices, and the broker provider.
          dotnet run -- universe fetch     Fetch and persist the current Nifty Microcap 250 constituent list.
          dotnet run -- scan [yyyy-MM-dd]  Run the two-stage Scanner (defaults to today if no date given).
          dotnet run -- reconcile          Re-fetch the trailing reconciliation window and correct any AdjClose drift (section 6.6). Run on a schedule (e.g. nightly).
          dotnet run -- benchmark [n]      Print the last n scan runs' Stage-1/Stage-2 counts and timings (section 23/25). Defaults to 10.
          dotnet run -- backtest [from] [to] [maxSymbols]
                                           Walk-forward backtest over the current universe (section 24). Dates default to
                                           the last 2 years ending today; maxSymbols defaults to 30 (see BacktestRequest's
                                           doc comment on why this is capped). Writes a Markdown + CSV report to
                                           ./backtest-reports/.

        NOTE: Before running any command against live data, run the Phase 0 smoke test
        first (tools/DataAccessSmokeTest) — see §24 Phase 0 in the build spec.
        """);
    return 0;
}

static async Task<int> RunDbMigrateAsync(IServiceProvider services)
{
    var runner = services.GetRequiredService<MigrationRunner>();
    Console.WriteLine("Applying database migrations...");
    await runner.ApplyMigrationsAsync();
    Console.WriteLine("Migrations applied successfully.");
    return 0;
}

static async Task<int> RunHealthCheckAsync(IServiceProvider services)
{
    Console.WriteLine("=== Provider Health Check ===");
    var allHealthy = true;

    var marketDataProviders = services.GetServices<IMarketDataProvider>();
    foreach (var provider in marketDataProviders)
    {
        var result = await provider.CheckHealthAsync();
        allHealthy &= result.IsHealthy;
        Console.WriteLine($"[{(result.IsHealthy ? "OK" : "FAIL")}] {provider.ProviderKind} market data — {result.Detail} (latency: {result.Latency})");
    }

    var universeProvider = services.GetRequiredService<IUniverseProvider>();
    var universeResult = await universeProvider.CheckHealthAsync();
    allHealthy &= universeResult.IsHealthy;
    Console.WriteLine($"[{(universeResult.IsHealthy ? "OK" : "FAIL")}] NSE Indices universe — {universeResult.Detail} (latency: {universeResult.Latency})");

    Console.WriteLine();
    Console.WriteLine(allHealthy ? "All providers healthy." : "One or more providers unhealthy — see above.");
    return allHealthy ? 0 : 1;
}

static async Task<int> RunUniverseFetchAsync(IServiceProvider services)
{
    var universeProvider = services.GetRequiredService<IUniverseProvider>();
    var symbolRepository = services.GetRequiredService<ISymbolRepository>();
    var universeRepository = services.GetRequiredService<IUniverseRepository>();

    Console.WriteLine("Fetching current Nifty Microcap 250 constituent list from NSE Indices...");
    var (effectiveDate, constituents) = await universeProvider.GetCurrentUniverseAsync();
    Console.WriteLine($"Fetched {constituents.Count} constituents as of {effectiveDate}.");

    var existingActive = await symbolRepository.GetAllActiveAsync();
    var nextSymbolId = existingActive.Count == 0 ? 1 : existingActive.Max(s => s.SymbolId) + 1;
    var symbolIds = new List<int>();

    foreach (var (nseSymbol, companyName, sector) in constituents)
    {
        var existing = await symbolRepository.GetByNseSymbolAsync(nseSymbol);
        var symbolId = existing?.SymbolId ?? nextSymbolId++;

        await symbolRepository.UpsertAsync(new NiftyMicrocapEngine.Domain.Symbol(symbolId, nseSymbol, companyName, sector ?? "", true));
        symbolIds.Add(symbolId);
    }

    var snapshotId = await universeRepository.SaveSnapshotAsync(
        new NiftyMicrocapEngine.Domain.UniverseSnapshot(0, effectiveDate, DateTimeOffset.UtcNow),
        symbolIds);

    Console.WriteLine($"Snapshot {snapshotId} persisted with {symbolIds.Count} members.");
    return 0;
}

static async Task<int> RunScanAsync(IServiceProvider services, string[] args)
{
    var asOfDate = DateOnly.FromDateTime(DateTime.UtcNow);
    if (args.Length > 1 && DateOnly.TryParse(args[1], out var parsed))
    {
        asOfDate = parsed;
    }

    var scanner = services.GetRequiredService<NiftyMicrocapEngine.Application.Scanning.IUniverseScanner>();

    Console.WriteLine($"Running scan for {asOfDate}...");
    var result = await scanner.RunAsync(asOfDate);

    Console.WriteLine();
    Console.WriteLine("=== Stage 1 (coarse) ===");
    Console.WriteLine($"Scanned: {result.Stage1SymbolsScanned}, excluded by data quality: {result.Stage1SymbolsExcludedByDataQuality}, duration: {result.Stage1Duration}");

    Console.WriteLine();
    Console.WriteLine($"=== Stage 2 (fine, top {result.Stage2ShortlistSize}) ===");
    Console.WriteLine($"Duration: {result.Stage2Duration}");
    Console.WriteLine();

    foreach (var candidate in result.Stage2Results.Take(20))
    {
        if (candidate.DecisionResult is null)
        {
            Console.WriteLine($"{candidate.NseSymbol,-15} EXCLUDED — {string.Join("; ", candidate.DataQualityFailureReasons)}");
            continue;
        }

        var outcome = candidate.DecisionResult.Outcome;
        var confidence = candidate.DecisionResult.ConfidenceScore;
        var rr = candidate.TradePlan?.RiskRewardRatio;
        Console.WriteLine($"{candidate.NseSymbol,-15} {outcome,-12} confidence={confidence:F1} R:R={rr?.ToString("F2") ?? "N/A"}");
    }

    return 0;
}

static async Task<int> RunReconcileAsync(IServiceProvider services)
{
    var job = services.GetRequiredService<NiftyMicrocapEngine.Application.DataAccess.ICorporateActionReconciliationJob>();

    Console.WriteLine("Running corporate-action reconciliation (section 6.6)...");
    var result = await job.RunAsync();

    Console.WriteLine($"Checked {result.SymbolsChecked} symbols ({result.SymbolsFailed} failed), duration {result.Duration}.");

    if (result.Overwrites.Count == 0)
    {
        Console.WriteLine("No AdjClose corrections needed.");
    }
    else
    {
        Console.WriteLine($"{result.Overwrites.Count} AdjClose correction(s) applied:");
        foreach (var overwrite in result.Overwrites)
        {
            Console.WriteLine($"  {overwrite.NseSymbol,-15} {overwrite.TradingDate:yyyy-MM-dd}  {overwrite.OldAdjClose} -> {overwrite.NewAdjClose}  ({overwrite.DivergenceFraction:P2})");
        }
    }

    return result.SymbolsFailed > 0 ? 1 : 0;
}

static async Task<int> RunBenchmarkAsync(IServiceProvider services, string[] args)
{
    var count = 10;
    if (args.Length > 1 && int.TryParse(args[1], out var parsed) && parsed > 0)
    {
        count = parsed;
    }

    var repository = services.GetRequiredService<NiftyMicrocapEngine.Application.Persistence.IScanHistoryRepository>();
    var records = await repository.GetRecentAsync(count);

    if (records.Count == 0)
    {
        Console.WriteLine("No scan history yet — run `dotnet run -- scan` at least once first.");
        return 0;
    }

    Console.WriteLine("=== Benchmark report — Stage 1/Stage 2 timings (section 23/25) ===");
    Console.WriteLine($"{"RunAt (UTC)",-26} {"Stage1Count",-12} {"Stage1(ms)",-11} {"Stage2Count",-12} {"Stage2(ms)",-11}");

    foreach (var record in records)
    {
        Console.WriteLine(
            $"{record.RunAt.UtcDateTime,-26:yyyy-MM-dd HH:mm:ss} {record.Stage1Count,-12} {record.Stage1DurationMs,-11} {record.Stage2Count,-12} {record.Stage2DurationMs,-11}");
    }

    // Section 23's target is "well under 5 minutes" for Stage 1 (all 250,
    // cached) and "under 5 minutes" for Stage 2 (30-symbol live intraday
    // fetch) — reported separately, never collapsed into one combined figure,
    // per the spec's explicit instruction.
    Console.WriteLine();
    Console.WriteLine($"Stage 1 average: {records.Average(r => r.Stage1DurationMs):F0}ms over {records.Count} run(s) (target: well under 300,000ms for a full 250-symbol universe).");
    Console.WriteLine($"Stage 2 average: {records.Average(r => r.Stage2DurationMs):F0}ms over {records.Count} run(s) (target: under 300,000ms for the shortlist).");

    return 0;
}

static async Task<int> RunBacktestAsync(IServiceProvider services, string[] args)
{
    var endDate = DateOnly.FromDateTime(DateTime.UtcNow);
    var startDate = endDate.AddYears(-2);
    var maxSymbols = 30;

    if (args.Length > 1 && DateOnly.TryParse(args[1], out var parsedStart)) startDate = parsedStart;
    if (args.Length > 2 && DateOnly.TryParse(args[2], out var parsedEnd)) endDate = parsedEnd;
    if (args.Length > 3 && int.TryParse(args[3], out var parsedMax) && parsedMax > 0) maxSymbols = parsedMax;

    var backtester = services.GetRequiredService<NiftyMicrocapEngine.Application.Backtesting.IWalkForwardBacktester>();

    Console.WriteLine($"Running walk-forward backtest {startDate} .. {endDate}, up to {maxSymbols} symbol(s)...");
    Console.WriteLine("This re-runs the full structure/indicator pipeline at every simulated as-of date per symbol — expect this to take a while for a wide date range or a high symbol count.");

    var request = new NiftyMicrocapEngine.Application.Backtesting.BacktestRequest(startDate, endDate, MaxSymbols: maxSymbols);
    var report = await backtester.RunAsync(request);

    Console.WriteLine();
    Console.WriteLine($"=== Backtest complete: {report.SymbolsWalked} symbol(s), {report.TotalAsOfDatesEvaluated} as-of date(s) evaluated, {report.TotalSignalsGenerated} Buy/StrongBuy signal(s), duration {report.Duration} ===");
    Console.WriteLine();

    foreach (var bucket in report.BucketStats)
    {
        Console.WriteLine($"{bucket.Decision,-10} signals={bucket.SignalCount,-4} simulated={bucket.TradesSimulated,-4} wins={bucket.Wins,-4} losses={bucket.Losses,-4} timedOut={bucket.TimedOut,-4} winRate={bucket.WinRate:P1} avgR={bucket.AverageRMultiple:F2} expectedR={bucket.ExpectedRPerSignal:F2}");
    }

    var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "backtest-reports");
    Directory.CreateDirectory(outputDir);
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

    var mdPath = Path.Combine(outputDir, $"backtest-{stamp}.md");
    var csvPath = Path.Combine(outputDir, $"backtest-{stamp}.csv");

    await File.WriteAllTextAsync(mdPath, NiftyMicrocapEngine.Application.Backtesting.BacktestReportFormatter.ToMarkdown(report));
    await File.WriteAllTextAsync(csvPath, NiftyMicrocapEngine.Application.Backtesting.BacktestReportFormatter.ToCsv(report));

    Console.WriteLine();
    Console.WriteLine($"Report written to {mdPath}");
    Console.WriteLine($"Trade-level CSV written to {csvPath}");

    return 0;
}
