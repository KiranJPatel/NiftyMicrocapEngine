using Microsoft.Extensions.Logging;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Infrastructure.BrokerData;

/// <summary>
/// Implements the §6.3 provider fallback policy: try Yahoo first for Daily/Weekly;
/// fall back to the broker provider on failure or a material gap. H1/M30/M15 go
/// straight to the broker provider (Yahoo doesn't serve those timeframes here).
///
/// Resolves each provider's symbol string via ISymbolRepository's SymbolMapping —
/// callers pass a domain SymbolId, never a provider-specific string — and stamps
/// the correct SymbolId onto every returned Candle (providers themselves return
/// SymbolId=0 as a sentinel, since they don't know the domain's numbering).
/// </summary>
public sealed class FallbackMarketDataRouter : IMarketDataRouter
{
    private readonly IMarketDataProvider _primary;
    private readonly IMarketDataProvider _secondary;
    private readonly ISymbolRepository _symbolRepository;
    private readonly ILogger<FallbackMarketDataRouter> _logger;

    public FallbackMarketDataRouter(
        IEnumerable<IMarketDataProvider> providers,
        ISymbolRepository symbolRepository,
        ILogger<FallbackMarketDataRouter> logger)
    {
        var providerList = providers.ToList();
        _primary = providerList.FirstOrDefault(p => p.ProviderKind == DataProviderKind.Yahoo)
            ?? throw new InvalidOperationException("No IMarketDataProvider registered for DataProviderKind.Yahoo (primary).");
        _secondary = providerList.FirstOrDefault(p => p.ProviderKind == DataProviderKind.Broker)
            ?? throw new InvalidOperationException("No IMarketDataProvider registered for DataProviderKind.Broker (secondary).");
        _symbolRepository = symbolRepository;
        _logger = logger;
    }

    public async Task<MarketDataFetchResult> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var asOf = DateOnly.FromDateTime(to.UtcDateTime);

        if (timeframe.IsConfirmationOnly())
        {
            var providerSymbol = await ResolveProviderSymbolAsync(symbolId, DataProviderKind.Broker, asOf, ct);
            var candles = await _secondary.GetCandlesAsync(providerSymbol, timeframe, from, to, ct);
            return new MarketDataFetchResult(StampSymbolId(candles, symbolId), Array.Empty<DataQualityFlag>());
        }

        IReadOnlyList<Candle>? primaryResult = null;
        Exception? primaryException = null;

        try
        {
            var yahooSymbol = await ResolveProviderSymbolAsync(symbolId, DataProviderKind.Yahoo, asOf, ct);
            primaryResult = await _primary.GetCandlesAsync(yahooSymbol, timeframe, from, to, ct);
        }
        catch (Exception ex)
        {
            primaryException = ex;
            _logger.LogWarning(ex, "Primary provider (Yahoo) failed for SymbolId={SymbolId}/{Timeframe}; falling back to secondary.", symbolId, timeframe);
        }

        var expectedCount = EstimateExpectedCandleCount(timeframe, from, to);
        var primaryLooksGappy = primaryResult is not null && expectedCount > 0 && primaryResult.Count < expectedCount * 0.7;

        if (primaryResult is { Count: > 0 } && !primaryLooksGappy)
        {
            return new MarketDataFetchResult(StampSymbolId(primaryResult, symbolId), Array.Empty<DataQualityFlag>());
        }

        _logger.LogInformation(
            "Falling back to secondary provider for SymbolId={SymbolId}/{Timeframe}. Primary exception: {HasException}, primary count: {Count}, expected approx: {Expected}.",
            symbolId, timeframe, primaryException is not null, primaryResult?.Count ?? 0, expectedCount);

        var brokerSymbol = await ResolveProviderSymbolAsync(symbolId, DataProviderKind.Broker, asOf, ct);
        var secondaryResult = await _secondary.GetCandlesAsync(brokerSymbol, timeframe, from, to, ct);

        var fallbackFlag = new DataQualityFlag(symbolId, asOf, "SecondaryProviderFallbackUsed",
            primaryException is not null
                ? $"Primary provider threw: {primaryException.Message}"
                : $"Primary provider returned insufficient candles ({primaryResult?.Count ?? 0} of ~{expectedCount} expected).");

        return new MarketDataFetchResult(StampSymbolId(secondaryResult, symbolId), new[] { fallbackFlag });
    }

    private async Task<string> ResolveProviderSymbolAsync(int symbolId, DataProviderKind provider, DateOnly asOf, CancellationToken ct)
    {
        var mapping = await _symbolRepository.GetActiveMappingAsync(symbolId, provider, asOf, ct);
        if (mapping is not null) return mapping.ExternalId;

        // Fallback: no explicit mapping recorded — for Yahoo, the NSE symbol + ".NS"
        // suffix is the overwhelmingly common case, so use that rather than failing
        // outright on every symbol that hasn't had an explicit mapping row inserted.
        var symbol = await _symbolRepository.GetBySymbolIdAsync(symbolId, ct)
            ?? throw new InvalidOperationException($"No Symbol found for SymbolId={symbolId}, and no SymbolMapping exists for provider {provider}.");

        if (provider == DataProviderKind.Yahoo)
        {
            return symbol.NseSymbol.EndsWith(".NS", StringComparison.OrdinalIgnoreCase) ? symbol.NseSymbol : $"{symbol.NseSymbol}.NS";
        }

        throw new InvalidOperationException(
            $"No SymbolMapping exists for SymbolId={symbolId}, provider={provider}. Broker providers require an " +
            "explicit instrument-token mapping (see ZerodhaMarketDataProvider's TODO) — there's no safe default to fall back to.");
    }

    private static IReadOnlyList<Candle> StampSymbolId(IReadOnlyList<Candle> candles, int symbolId) =>
        candles.Select(c => c with { SymbolId = symbolId }).ToList();

    private static int EstimateExpectedCandleCount(Timeframe timeframe, DateTimeOffset from, DateTimeOffset to)
    {
        var totalDays = (to - from).TotalDays;
        if (totalDays <= 0) return 0;

        return timeframe switch
        {
            Timeframe.Weekly => Math.Max(1, (int)(totalDays / 7)),
            Timeframe.Daily => Math.Max(1, (int)(totalDays * (5.0 / 7.0))),
            _ => 0
        };
    }
}
