using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.DataQuality;

/// <summary>
/// Implements build spec section 6.7: before a symbol enters analysis, checks
/// minimum non-zero-volume trading days in a trailing window, maximum
/// consecutive no-trade/circuit-locked days, and calendar gaps beyond
/// holidays. A failing symbol is excluded and flagged with the SPECIFIC
/// reason, never silently dropped — the caller (Scanner) must surface
/// DataQualityGateResult.FailureReasons somewhere auditable (e.g. the
/// DataQualityFlags table) rather than just skipping the symbol quietly.
/// </summary>
public interface IDataQualityGate
{
    DataQualityGateResult Evaluate(IReadOnlyList<Candle> trailingDailyCandles, IReadOnlyList<DateOnly> expectedTradingDays);
}

public sealed record DataQualityGateResult(bool Passed, IReadOnlyList<string> FailureReasons);
