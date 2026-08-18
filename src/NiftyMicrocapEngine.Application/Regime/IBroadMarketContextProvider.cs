using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Regime;

/// <summary>
/// Supplies the broad-market context (Nifty 50 / Nifty Midcap trend state for
/// the Regime Filter, plus the two benchmark candle series for Relative
/// Strength) that the Scanner needs once per scan run — not once per symbol,
/// since these are shared across every candidate in the universe. Computed
/// once at the start of RunAsync and reused for all Stage 1/Stage 2 symbol
/// evaluations, rather than each symbol independently re-fetching and
/// re-analyzing the same two index series 250 times.
/// </summary>
public interface IBroadMarketContextProvider
{
    Task<BroadMarketContext> GetContextAsync(DateOnly asOfDate, CancellationToken ct = default);
}

public sealed record BroadMarketContext(
    BroadMarketState RegimeState,
    IReadOnlyList<Candle> Nifty50Candles,
    IReadOnlyList<Candle> NiftyMicrocap250Candles);
