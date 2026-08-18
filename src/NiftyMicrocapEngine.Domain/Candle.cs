namespace NiftyMicrocapEngine.Domain;

/// <summary>
/// A single closed candle. Matches build spec §5.1 exactly — SymbolId is an int FK
/// into Symbols (§18 schema), not a bare string, and AdjClose is tracked separately
/// from Close because Yahoo retroactively rewrites adjusted-close on corporate
/// actions (§6.6 reconciliation depends on this distinction existing).
///
/// This record intentionally carries only the raw fields NSE/providers actually
/// supply. Derived values (True Range, ATR, Gap, Body%, etc — §5.1) are NOT stored
/// on the record itself; they're computed by CandleSeriesCalculator because several
/// require the prior candle, which a single immutable record can't reference.
/// </summary>
public sealed record Candle(
    int SymbolId,
    Timeframe Timeframe,
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal AdjClose,
    long Volume)
{
    /// <summary>
    /// Validates OHLC invariants. Called explicitly by callers that construct Candles
    /// from external data (providers) — not run implicitly on every construction, since
    /// some internal code (tests, backtesting fixtures) intentionally builds edge-case
    /// candles that a blanket constructor-time check would block.
    /// </summary>
    public void Validate()
    {
        if (High < Low)
            throw new InvalidOperationException(
                $"Candle invariant violated for SymbolId={SymbolId} {Timestamp:O}: High ({High}) < Low ({Low}).");
        if (Open < Low || Open > High)
            throw new InvalidOperationException(
                $"Candle invariant violated for SymbolId={SymbolId} {Timestamp:O}: Open ({Open}) outside [Low, High].");
        if (Close < Low || Close > High)
            throw new InvalidOperationException(
                $"Candle invariant violated for SymbolId={SymbolId} {Timestamp:O}: Close ({Close}) outside [Low, High].");
        if (Volume < 0)
            throw new InvalidOperationException(
                $"Candle invariant violated for SymbolId={SymbolId} {Timestamp:O}: Volume ({Volume}) is negative.");
        if (Open <= 0 || High <= 0 || Low <= 0 || Close <= 0 || AdjClose <= 0)
            throw new InvalidOperationException(
                $"Candle invariant violated for SymbolId={SymbolId} {Timestamp:O}: all prices must be strictly positive.");
    }
}
