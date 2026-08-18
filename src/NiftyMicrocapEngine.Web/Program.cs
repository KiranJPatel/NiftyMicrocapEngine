using NiftyMicrocapEngine.Application.Backtesting;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.DataQuality;
using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.MultiTimeframe;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Application.Scanning;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Infrastructure.BrokerData;
using NiftyMicrocapEngine.Infrastructure.Persistence;
using NiftyMicrocapEngine.Infrastructure.YahooFinance;
using NiftyMicrocapEngine.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

builder.Services.Configure<DataProvidersOptions>(builder.Configuration.GetSection(DataProvidersOptions.SectionName));
builder.Services.Configure<NseIndicesOptions>(builder.Configuration.GetSection(NseIndicesOptions.SectionName));
builder.Services.Configure<BenchmarkIndicesOptions>(builder.Configuration.GetSection(BenchmarkIndicesOptions.SectionName));
builder.Services.Configure<ReconciliationOptions>(builder.Configuration.GetSection(ReconciliationOptions.SectionName));
builder.Services.Configure<DataQualityGateOptions>(builder.Configuration.GetSection(DataQualityGateOptions.SectionName));
builder.Services.Configure<StructureThresholds>(builder.Configuration.GetSection(StructureThresholds.SectionName));
builder.Services.Configure<MultiTimeframeOptions>(builder.Configuration.GetSection(MultiTimeframeOptions.SectionName));
builder.Services.Configure<DecisionEngineOptions>(builder.Configuration.GetSection(DecisionEngineOptions.SectionName));
builder.Services.Configure<RelativeStrengthOptions>(builder.Configuration.GetSection(RelativeStrengthOptions.SectionName));
builder.Services.Configure<RiskManagerOptions>(builder.Configuration.GetSection(RiskManagerOptions.SectionName));
builder.Services.Configure<ScannerOptions>(builder.Configuration.GetSection(ScannerOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

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
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<IMultiTimeframeEngine, MultiTimeframeEngine>();
builder.Services.AddSingleton<IRegimeFilter, RegimeFilter>();
builder.Services.AddSingleton<IRelativeStrengthCalculator, RelativeStrengthCalculator>();
builder.Services.AddSingleton<IDecisionEngine, DecisionEngine>();
builder.Services.AddSingleton<ITradePlanBuilder, TradePlanBuilder>();
builder.Services.AddSingleton<IPortfolioRiskManager, PortfolioRiskManager>();
builder.Services.AddSingleton<IDataQualityGate, DataQualityGate>();
builder.Services.AddSingleton<ICircuitBandTracker, CircuitBandTracker>();
builder.Services.AddSingleton<ICandlePsychologyAnalyzer>(_ => new CandlePsychologyAnalyzer());
builder.Services.AddScoped<ICachingMarketDataService, CachingMarketDataService>();
builder.Services.AddSingleton<IBroadMarketContextProvider, BroadMarketContextProvider>();
builder.Services.AddScoped<ICorporateActionReconciliationJob, CorporateActionReconciliationJob>();
builder.Services.AddScoped<IUniverseScanner, UniverseScanner>();
builder.Services.AddScoped<IWalkForwardBacktester, WalkForwardBacktester>();
builder.Services.AddHostedService<ReconciliationSchedulerHostedService>();
builder.Services.AddHostedService<ScanSchedulerHostedService>();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/scan", DashboardEndpoints.RunScan);
app.MapGet("/api/scan/{symbolId:int}", DashboardEndpoints.GetDrillDown);
app.MapGet("/api/chart/{symbolId:int}", DashboardEndpoints.GetChart);
app.MapPost("/api/reconcile", DashboardEndpoints.RunReconciliation);
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();
