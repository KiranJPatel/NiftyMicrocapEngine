using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.DataAccess;

/// <summary>
/// Implements build spec section 17's "cached + incrementally-updated" data
/// strategy for Stage 1/Stage 2: check ICandleRepository's cache first, fetch
/// only the delta beyond the latest cached candle via IMarketDataRouter, merge
/// and persist the result, and return the full requested range from the
/// cache. This is what makes repeated scans over the same historical window
/// cheap — a fresh 250-symbol universe scan should only pay the live-fetch
/// cost for genuinely new candles (typically 1 trading day per symbol on a
/// daily cadence), not the full 2-year lookback every time.
///
/// This sits between the Scanner and IMarketDataRouter — the Scanner should
/// call this instead of IMarketDataRouter directly for any candle fetch it
/// wants cached (i.e. everything except one-off ad-hoc lookups).
/// </summary>
public interface ICachingMarketDataService
{
    Task<IReadOnlyList<Candle>> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
