using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.DataAccess;

/// <summary>
/// Port for any source of Candle data. Implemented by the Yahoo Finance provider
/// (primary, §6.2 — Daily/Weekly only) and the broker-data provider (secondary/
/// fallback and the sole source for H1/M30/M15 confirmation timeframes, per §6.2's
/// note that Yahoo's intraday retention is too short for this engine's needs).
/// </summary>
public interface IMarketDataProvider
{
    DataProviderKind ProviderKind { get; }

    /// <summary>
    /// Fetches candles for one symbol/timeframe over [from, to]. Returns an empty
    /// list (not a throw) if there's no data in range. Throws only for transport/
    /// auth/unexpected-shape failures.
    /// </summary>
    Task<IReadOnlyList<Candle>> GetCandlesAsync(string providerSymbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    Task<ProviderHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
}

public sealed record ProviderHealthCheckResult(bool IsHealthy, string Detail, TimeSpan? Latency = null);

/// <summary>
/// Orchestrates retrieval across primary (Yahoo) and secondary (broker) providers
/// per §6.3's fallback policy, returning both the candles and any DataQualityFlags
/// generated in the process (e.g. fallback-used, provider-returned-gap).
/// </summary>
public interface IMarketDataRouter
{
    Task<MarketDataFetchResult> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

public sealed record MarketDataFetchResult(IReadOnlyList<Candle> Candles, IReadOnlyList<DataQualityFlag> QualityFlags);

/// <summary>
/// Port for fetching the Nifty Microcap 250 constituent list from NSE Indices (§6.5).
/// </summary>
public interface IUniverseProvider
{
    Task<(DateOnly EffectiveDate, IReadOnlyList<(string NseSymbol, string CompanyName, string? Sector)> Constituents)> GetCurrentUniverseAsync(CancellationToken ct = default);
    Task<ProviderHealthCheckResult> CheckHealthAsync(CancellationToken ct = default);
}
