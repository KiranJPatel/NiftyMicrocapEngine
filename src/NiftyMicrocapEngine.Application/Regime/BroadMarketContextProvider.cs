using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Regime;

/// <summary>
/// Fetches Nifty 50, Nifty Midcap, and Nifty Microcap 250 index candles
/// directly via the primary (Yahoo) IMarketDataProvider using the symbols
/// from BenchmarkIndicesOptions, runs the two trend-classification series
/// (Nifty 50, Nifty Midcap) through the same structure engine used for
/// individual equities, and classifies each into a BroadMarketTrendState per
/// section 13's Bear/StrongBear suppression rule.
///
/// Index data is fetched directly from the primary provider rather than
/// through IMarketDataRouter/ICachingMarketDataService, since indices have no
/// SymbolId/SymbolMapping row in this engine's Symbols table (they're not
/// tradable universe members) — the router's symbol-resolution path doesn't
/// apply to them. A future pass could extend the caching layer to key by
/// provider-symbol string directly rather than SymbolId, which would let
/// indices benefit from the same incremental-fetch caching equities get;
/// not done here to avoid widening ICachingMarketDataService's contract for
/// a single caller.
/// </summary>
public sealed class BroadMarketContextProvider : IBroadMarketContextProvider
{
    private readonly IMarketDataProvider _primaryProvider;
    private readonly BenchmarkIndicesOptions _options;
    private readonly StructureThresholds _structureThresholds;
    private readonly ILogger<BroadMarketContextProvider> _logger;

    public BroadMarketContextProvider(
        IEnumerable<IMarketDataProvider> providers,
        IOptions<BenchmarkIndicesOptions> options,
        IOptions<StructureThresholds> structureThresholds,
        ILogger<BroadMarketContextProvider> logger)
    {
        _primaryProvider = providers.FirstOrDefault(p => p.ProviderKind == DataProviderKind.Yahoo)
            ?? throw new InvalidOperationException("No IMarketDataProvider registered for DataProviderKind.Yahoo — required for broad-market index data.");
        _options = options.Value;
        _structureThresholds = structureThresholds.Value;
        _logger = logger;
    }

    public async Task<BroadMarketContext> GetContextAsync(DateOnly asOfDate, CancellationToken ct = default)
    {
        var to = asOfDate.ToUtcDateTimeOffset(TimeOnly.MaxValue);
        var from = asOfDate.AddYears(-1).ToUtcDateTimeOffset(TimeOnly.MinValue);

        var nifty50Candles = await FetchSafelyAsync(_options.Nifty50YahooSymbol, from, to, ct);
        var niftyMidcapCandles = await FetchSafelyAsync(_options.NiftyMidcapYahooSymbol, from, to, ct);
        var niftyMicrocap250Candles = await FetchSafelyAsync(_options.NiftyMicrocap250YahooSymbol, from, to, ct);

        var nifty50Trend = await ClassifyTrendAsync(nifty50Candles);
        var niftyMidcapTrend = await ClassifyTrendAsync(niftyMidcapCandles);

        var regimeState = new BroadMarketState(nifty50Trend, niftyMidcapTrend, asOfDate);

        return new BroadMarketContext(regimeState, nifty50Candles, niftyMicrocap250Candles);
    }

    private async Task<IReadOnlyList<Candle>> FetchSafelyAsync(string providerSymbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        try
        {
            return await _primaryProvider.GetCandlesAsync(providerSymbol, Timeframe.Daily, from, to, ct);
        }
        catch (Exception ex)
        {
            // A failed benchmark fetch must not take down the whole scan — the
            // Regime Filter and Relative Strength layers both already handle
            // an empty/insufficient series safely (Neutral regime, null RS
            // ratios), so degrade gracefully rather than propagate.
            _logger.LogWarning(ex, "Failed to fetch benchmark index {Symbol}; broad-market context for it will be unavailable this run.", providerSymbol);
            return Array.Empty<Candle>();
        }
    }

    /// <summary>
    /// Classifies an index's trend into the five-state BroadMarketTrendState
    /// using the same structure engine's PrevailingTrend plus recent price
    /// momentum for the Strong/plain distinction. Section 13 only strictly
    /// requires distinguishing Bear/StrongBear from everything else (that's
    /// what actually triggers suppression), so the Strong/plain split here
    /// uses a straightforward heuristic: StrongBear/StrongBull when the
    /// structure engine's PrevailingTrend agrees AND price is beyond 1 ATR
    /// from its 20-period SMA in the same direction (meaningfully extended,
    /// not just barely trending); otherwise the plain Bear/Bull/Neutral.
    /// </summary>
    private async Task<BroadMarketTrendState> ClassifyTrendAsync(IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0) return BroadMarketTrendState.Neutral;

        var handles = StructureAnalysisPipelineFactory.Create(symbolId: 0, Timeframe.Daily, _structureThresholds);
        foreach (var candle in candles.OrderBy(c => c.Timestamp))
        {
            await handles.Pipeline.RunAsync(candle, CancellationToken.None);
        }

        var trend = handles.StructureBreaks.PrevailingTrend;
        var atr = handles.Atr.CurrentValue;
        var latestClose = candles[^1].Close;

        if (trend == TrendDirection.Ranging || atr is null or 0m) return BroadMarketTrendState.Neutral;

        var sma20 = candles.TakeLast(20).Select(c => c.Close).DefaultIfEmpty(latestClose).Average();
        var distanceFromSmaInAtr = Math.Abs(latestClose - sma20) / atr.Value;
        var isExtended = distanceFromSmaInAtr >= 1m;

        if (trend == TrendDirection.Bearish) return isExtended ? BroadMarketTrendState.StrongBear : BroadMarketTrendState.Bear;
        return isExtended ? BroadMarketTrendState.StrongBull : BroadMarketTrendState.Bull;
    }
}
