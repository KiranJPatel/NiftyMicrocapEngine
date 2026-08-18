using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Regime;

/// <summary>
/// Computes return-ratio (symbol return / benchmark return, over the same
/// lookback window) vs the Nifty Microcap 250 index and vs Nifty 50, at both the
/// short (default 20 trading days) and long (default 60 trading days) lookback.
/// Returns null for any ratio where either series has insufficient trailing
/// history for that lookback — never a fabricated/interpolated value, matching
/// the no-fabrication convention used throughout this codebase (the audited
/// trade-duration fix, ATR/RSI warmup nulls, etc).
/// </summary>
public sealed class RelativeStrengthCalculator : IRelativeStrengthCalculator
{
    private readonly RelativeStrengthOptions _options;

    public RelativeStrengthCalculator(IOptions<RelativeStrengthOptions> options)
    {
        _options = options.Value;
    }

    public RelativeStrengthResult Calculate(
        IReadOnlyList<Candle> symbolCandles,
        IReadOnlyList<Candle> niftyMicrocap250Candles,
        IReadOnlyList<Candle> nifty50Candles)
    {
        return new RelativeStrengthResult(
            ReturnRatioVsMicrocap250Short: ComputeRatio(symbolCandles, niftyMicrocap250Candles, _options.LookbackDaysShort),
            ReturnRatioVsMicrocap250Long: ComputeRatio(symbolCandles, niftyMicrocap250Candles, _options.LookbackDaysLong),
            ReturnRatioVsNifty50Short: ComputeRatio(symbolCandles, nifty50Candles, _options.LookbackDaysShort),
            ReturnRatioVsNifty50Long: ComputeRatio(symbolCandles, nifty50Candles, _options.LookbackDaysLong));
    }

    /// <summary>
    /// Both series are assumed ordered ascending (oldest-first), aligned by trailing
    /// index (not by date-matching) — callers should have already reconciled the
    /// two series onto the same trading calendar upstream (§6.6 reconciliation).
    /// </summary>
    private static decimal? ComputeRatio(IReadOnlyList<Candle> symbolCandles, IReadOnlyList<Candle> benchmarkCandles, int lookbackDays)
    {
        if (symbolCandles.Count <= lookbackDays || benchmarkCandles.Count <= lookbackDays)
            return null;

        var symbolStart = symbolCandles[^(lookbackDays + 1)].AdjClose;
        var symbolEnd = symbolCandles[^1].AdjClose;
        var benchmarkStart = benchmarkCandles[^(lookbackDays + 1)].AdjClose;
        var benchmarkEnd = benchmarkCandles[^1].AdjClose;

        if (symbolStart <= 0 || benchmarkStart <= 0) return null;

        var symbolReturn = (symbolEnd - symbolStart) / symbolStart;
        var benchmarkReturn = (benchmarkEnd - benchmarkStart) / benchmarkStart;

        // A benchmark return of exactly zero makes the ratio undefined (division by
        // zero) rather than infinite/fabricated — report null and let the caller's
        // scoring layer treat it as "insufficient basis for RS comparison this period."
        if (benchmarkReturn == 0) return null;

        return symbolReturn / benchmarkReturn;
    }
}
