using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.Scanning;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Web;

/// <summary>
/// Runs IUniverseScanner (§17) once every 24 hours at a configured IST
/// hour. Closes the last piece of Phase 7's automation gap the README's
/// "Still open" list flagged — the Scanner itself previously had no
/// schedule at all (only reconciliation did), so a production deployment
/// needed an external scheduler for the scan run.
///
/// Same opt-in-by-default reasoning as ReconciliationSchedulerHostedService:
/// ScannerOptions.ScheduledEnabled defaults to false, since a hosted service
/// silently running a full live scan (which hits Yahoo/NSE for every
/// universe symbol) on an implicit schedule is worse than one requiring an
/// explicit flip in appsettings.
///
/// IUniverseScanner is registered Scoped, so this creates a fresh DI scope
/// per run via IServiceScopeFactory rather than resolving it once at
/// startup — same pattern as ReconciliationSchedulerHostedService.
/// </summary>
public sealed class ScanSchedulerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScannerOptions _options;
    private readonly ILogger<ScanSchedulerHostedService> _logger;

    public ScanSchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<ScannerOptions> options,
        ILogger<ScanSchedulerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ScheduledEnabled)
        {
            _logger.LogInformation(
                "Scanner:ScheduledEnabled is false — the scan will only run when invoked manually (CLI `scan` or the dashboard). Set Scanner:ScheduledEnabled to true in appsettings to run it daily at Scanner:ScheduledHourIst.");
            return;
        }

        _logger.LogInformation(
            "Scan scheduler started — will run daily at {Hour}:00 IST.", _options.ScheduledHourIst);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IstDailyScheduler.TimeUntilNextRun(DateTimeOffset.UtcNow, _options.ScheduledHourIst);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var scanner = scope.ServiceProvider.GetRequiredService<IUniverseScanner>();

        // asOfDate is computed AT TRIGGER TIME, in IST — matches the rest of
        // this codebase's "today" convention (IndiaStandardTime) rather than
        // the UTC calendar date, which would be wrong for a chunk of the IST day.
        var nowIst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, IndiaStandardTime.TimeZone);
        var asOfDate = DateOnly.FromDateTime(nowIst.DateTime);

        try
        {
            _logger.LogInformation("Scheduled scan run starting for {AsOfDate}.", asOfDate);
            var result = await scanner.RunAsync(asOfDate, ct);
            _logger.LogInformation(
                "Scheduled scan run complete for {AsOfDate}: {Stage1Count} symbol(s) scanned in Stage 1, {Stage2Count} in Stage 2's ranked shortlist, duration {Stage1Duration} + {Stage2Duration}.",
                asOfDate, result.Stage1SymbolsScanned, result.Stage2Results.Count, result.Stage1Duration, result.Stage2Duration);
        }
        catch (Exception ex)
        {
            // A failed scheduled run must not take the whole hosted service
            // down — the loop above needs to keep going so tomorrow's run
            // still happens.
            _logger.LogError(ex, "Scheduled scan run failed for {AsOfDate}. Will retry at the next scheduled time.", asOfDate);
        }
    }
}
