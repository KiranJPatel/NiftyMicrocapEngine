using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.Risk;

namespace NiftyMicrocapEngine.Application.Scanning;

/// <summary>
/// One symbol's outcome from a scan pass — the Decision Engine result plus
/// (if approved) the Risk Manager's trade plan, plus whichever stage produced
/// this result and any exclusion reason if the symbol never reached scoring.
/// </summary>
public sealed record ScanCandidateResult(
    int SymbolId,
    string NseSymbol,
    ScanStage ReachedStage,
    DecisionEngineResult? DecisionResult,
    TradePlan? TradePlan,
    bool ExcludedByDataQualityGate,
    IReadOnlyList<string> DataQualityFailureReasons);

public enum ScanStage { Stage1CoarseOnly, Stage2FineConfirmed }

public sealed record ScanRunResult(
    DateOnly AsOfDate,
    int Stage1SymbolsScanned,
    int Stage1SymbolsExcludedByDataQuality,
    int Stage2ShortlistSize,
    IReadOnlyList<ScanCandidateResult> Stage1Results,
    IReadOnlyList<ScanCandidateResult> Stage2Results,
    TimeSpan Stage1Duration,
    TimeSpan Stage2Duration);

/// <summary>
/// Implements build spec section 17's two-stage funnel. Stage 1 (coarse): full
/// universe, Daily/Weekly only, cached and incrementally updated data — cheap,
/// mostly local computation. Stage 2 (fine): top-N candidates from Stage 1
/// (default 30) get intraday (H1/M30/M15) data fetched and the MTF engine's
/// full intraday weighting applied. Stage 2 output is ranked by confidence,
/// then momentum, trend, risk-reward, expected return, and relative strength.
/// </summary>
public interface IUniverseScanner
{
    Task<ScanRunResult> RunAsync(DateOnly asOfDate, CancellationToken ct = default);
}
