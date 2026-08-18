using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.DataAccess;

/// <summary>
/// Implements section 6.6 exactly: re-fetch the trailing N days of Daily
/// candles for every active symbol via the primary (Yahoo) provider directly
/// — not through ICachingMarketDataService, since that service would treat
/// already-cached dates as "already have this, skip" and never re-request
/// them, defeating the entire point of this job. Every AdjClose divergence
/// beyond tolerance is logged as an audit trail (symbol, date, old/new
/// value) before being persisted, per the spec's explicit requirement.
/// </summary>
public sealed class CorporateActionReconciliationJob : ICorporateActionReconciliationJob
{
    private readonly ISymbolRepository _symbolRepository;
    private readonly ICandleRepository _candleRepository;
    private readonly IMarketDataProvider _primaryProvider;
    private readonly ReconciliationOptions _options;
    private readonly ILogger<CorporateActionReconciliationJob> _logger;

    public CorporateActionReconciliationJob(
        ISymbolRepository symbolRepository,
        ICandleRepository candleRepository,
        IEnumerable<IMarketDataProvider> providers,
        IOptions<ReconciliationOptions> options,
        ILogger<CorporateActionReconciliationJob> logger)
    {
        _symbolRepository = symbolRepository;
        _candleRepository = candleRepository;
        _primaryProvider = providers.FirstOrDefault(p => p.ProviderKind == DataProviderKind.Yahoo)
            ?? throw new InvalidOperationException("No IMarketDataProvider registered for DataProviderKind.Yahoo — required for reconciliation.");
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ReconciliationRunResult> RunAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var symbols = await _symbolRepository.GetAllActiveAsync(ct);

        var overwrites = new List<AdjCloseOverwrite>();
        var failedCount = 0;

        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-_options.LookbackDays);

        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var symbolOverwrites = await ReconcileOneSymbolAsync(symbol, from, to, ct);
                overwrites.AddRange(symbolOverwrites);
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogWarning(ex, "Reconciliation failed for {NseSymbol} (SymbolId={SymbolId}) — will retry on next scheduled run.", symbol.NseSymbol, symbol.SymbolId);
            }
        }

        sw.Stop();

        _logger.LogInformation(
            "Corporate-action reconciliation complete: {Checked} symbols checked, {Failed} failed, {OverwriteCount} AdjClose corrections applied, duration {Duration}.",
            symbols.Count, failedCount, overwrites.Count, sw.Elapsed);

        return new ReconciliationRunResult(symbols.Count, failedCount, overwrites, sw.Elapsed);
    }

    private async Task<List<AdjCloseOverwrite>> ReconcileOneSymbolAsync(Symbol symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var providerSymbol = symbol.NseSymbol.EndsWith(".NS", StringComparison.OrdinalIgnoreCase) ? symbol.NseSymbol : $"{symbol.NseSymbol}.NS";

        var freshCandles = await _primaryProvider.GetCandlesAsync(providerSymbol, Timeframe.Daily, from, to, ct);
        if (freshCandles.Count == 0) return new List<AdjCloseOverwrite>();

        var cachedCandles = await _candleRepository.GetCandlesAsync(symbol.SymbolId, Timeframe.Daily, from, to, ct);
        var cachedByDate = cachedCandles.ToDictionary(c => c.Timestamp);

        var overwrites = new List<AdjCloseOverwrite>();
        var toSave = new List<Candle>();

        foreach (var fresh in freshCandles)
        {
            var stampedFresh = fresh with { SymbolId = symbol.SymbolId };

            if (!cachedByDate.TryGetValue(stampedFresh.Timestamp, out var cached))
            {
                // Not previously cached at all (e.g. reconciliation window
                // extends before this symbol's cache started) — persist it,
                // but that's a normal backfill, not an AdjClose correction,
                // so it doesn't get logged as an overwrite.
                toSave.Add(stampedFresh);
                continue;
            }

            if (cached.AdjClose == 0) continue; // avoid a divide-by-zero on a pathological cached zero

            var divergence = Math.Abs(stampedFresh.AdjClose - cached.AdjClose) / cached.AdjClose;
            if (divergence > _options.AdjCloseToleranceFraction)
            {
                overwrites.Add(new AdjCloseOverwrite(symbol.SymbolId, symbol.NseSymbol, stampedFresh.Timestamp, cached.AdjClose, stampedFresh.AdjClose, divergence));
                toSave.Add(stampedFresh);
            }
        }

        if (toSave.Count > 0)
        {
            await _candleRepository.SaveCandlesAsync(toSave, ct);
        }

        foreach (var overwrite in overwrites)
        {
            // Explicit per-overwrite audit log line, per section 6.6's
            // "log every overwrite (symbol, date, old/new value)" requirement
            // — not just an aggregate count, since an operator investigating
            // a specific symbol needs to find this in the log stream.
            _logger.LogInformation(
                "AdjClose reconciliation: {NseSymbol} {TradingDate:yyyy-MM-dd} {OldAdjClose} -> {NewAdjClose} (divergence {Divergence:P2}).",
                overwrite.NseSymbol, overwrite.TradingDate, overwrite.OldAdjClose, overwrite.NewAdjClose, overwrite.DivergenceFraction);
        }

        return overwrites;
    }
}
