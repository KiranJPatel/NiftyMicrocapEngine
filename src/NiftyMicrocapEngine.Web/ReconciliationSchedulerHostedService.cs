using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;

namespace NiftyMicrocapEngine.Web;

/// <summary>
/// Runs ICorporateActionReconciliationJob (§6.6) once every 24 hours at a
/// configured IST hour. Closes the Phase 7 "reconciliation job running on
/// schedule" gap the README's "Still open" list flagged — previously the job
/// only ran when someone manually hit `dotnet run -- reconcile` or the
/// dashboard's /api/reconcile endpoint.
///
/// Opt-in by design: ReconciliationOptions.ScheduledEnabled defaults to
/// false, so this service is inert (logs once and exits its loop) unless the
/// operator explicitly turns it on in appsettings — see that option's doc
/// comment for why.
///
/// ICorporateActionReconciliationJob is registered Scoped (it depends on
/// scoped repositories), so — same as any BackgroundService touching scoped
/// services — this creates a fresh DI scope per run via IServiceScopeFactory
/// rather than resolving the job once at startup.
/// </summary>
public sealed class ReconciliationSchedulerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReconciliationOptions _options;
    private readonly ILogger<ReconciliationSchedulerHostedService> _logger;

    public ReconciliationSchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<ReconciliationOptions> options,
        ILogger<ReconciliationSchedulerHostedService> logger)
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
                "Reconciliation:ScheduledEnabled is false — the reconciliation job will only run when invoked manually (CLI `reconcile` or the dashboard endpoint). Set Reconciliation:ScheduledEnabled to true in appsettings to run it nightly at Reconciliation:ScheduledHourIst.");
            return;
        }

        _logger.LogInformation(
            "Reconciliation scheduler started — will run daily at {Hour}:00 IST.", _options.ScheduledHourIst);

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
        var job = scope.ServiceProvider.GetRequiredService<ICorporateActionReconciliationJob>();

        try
        {
            _logger.LogInformation("Scheduled reconciliation run starting.");
            var result = await job.RunAsync(ct);
            _logger.LogInformation(
                "Scheduled reconciliation run complete: {Checked} symbol(s) checked, {Failed} failed, {Corrections} AdjClose correction(s), duration {Duration}.",
                result.SymbolsChecked, result.SymbolsFailed, result.Overwrites.Count, result.Duration);
        }
        catch (Exception ex)
        {
            // A failed scheduled run must not take the whole hosted service
            // down — the loop above needs to keep going so tomorrow's run
            // still happens.
            _logger.LogError(ex, "Scheduled reconciliation run failed. Will retry at the next scheduled time.");
        }
    }
}
