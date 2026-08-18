namespace NiftyMicrocapEngine.Application.DataAccess;

/// <summary>
/// Implements build spec section 6.6: a scheduled job that re-fetches the
/// trailing N days (default 90) of Daily candles for every active symbol via
/// Yahoo, and overwrites stored AdjClose where it diverges from the cached
/// value beyond a configurable tolerance (default 0.1%). This is the only
/// correct way to catch Yahoo's retroactive adjusted-close rewrites (splits,
/// bonuses, dividends applied after the fact) without re-downloading each
/// symbol's full history on every run.
///
/// Distinct from ICachingMarketDataService's delta-fetch: that service only
/// fetches candles BEYOND the cached range and never revisits already-cached
/// dates, so it would never see a retroactive AdjClose correction on a date
/// it already has cached. This job deliberately re-fetches a fixed trailing
/// window regardless of what's cached, specifically to catch that class of
/// correction.
/// </summary>
public interface ICorporateActionReconciliationJob
{
    Task<ReconciliationRunResult> RunAsync(CancellationToken ct = default);
}

public sealed record AdjCloseOverwrite(int SymbolId, string NseSymbol, DateTimeOffset TradingDate, decimal OldAdjClose, decimal NewAdjClose, decimal DivergenceFraction);

public sealed record ReconciliationRunResult(
    int SymbolsChecked,
    int SymbolsFailed,
    IReadOnlyList<AdjCloseOverwrite> Overwrites,
    TimeSpan Duration);
