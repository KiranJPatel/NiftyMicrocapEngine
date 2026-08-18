using NiftyMicrocapEngine.Application.Decision;
using NiftyMicrocapEngine.Application.Risk;
using NiftyMicrocapEngine.Application.Scanning;

namespace NiftyMicrocapEngine.Web;

public sealed record ScanRowDto(
    int SymbolId,
    string NseSymbol,
    string Decision,
    decimal Confidence,
    decimal? RiskReward,
    decimal? RelativeStrength,
    bool ExcludedByDataQuality);

public sealed record ScanResponseDto(
    string AsOfDate,
    int Stage1Scanned,
    int Stage1Excluded,
    int Stage2ShortlistSize,
    string Stage1DurationMs,
    string Stage2DurationMs,
    List<ScanRowDto> Rows);

public sealed record LayerScoreDto(string LayerName, decimal MaxPoints, decimal Contribution);

public sealed record TradePlanDto(
    decimal Entry,
    decimal StopLoss,
    decimal Target1,
    decimal Target2,
    decimal Target3,
    decimal RiskPercent,
    decimal RiskRewardRatio,
    string InvalidationLevel,
    string? EstimatedDuration,
    string? DurationDataQualityFlag);

public sealed record DrillDownResponseDto(
    int SymbolId,
    string NseSymbol,
    string Decision,
    decimal Confidence,
    string ReasoningText,
    List<LayerScoreDto> LayerScores,
    List<string> HardGateFailures,
    TradePlanDto? TradePlan,
    bool ExcludedByDataQuality,
    List<string> DataQualityFailureReasons);

public static class DashboardMapping
{
    public static ScanResponseDto ToScanResponse(ScanRunResult result)
    {
        var rows = result.Stage2Results.Select(ToRow).ToList();

        return new ScanResponseDto(
            result.AsOfDate.ToString("yyyy-MM-dd"),
            result.Stage1SymbolsScanned,
            result.Stage1SymbolsExcludedByDataQuality,
            result.Stage2ShortlistSize,
            result.Stage1Duration.TotalMilliseconds.ToString("F0"),
            result.Stage2Duration.TotalMilliseconds.ToString("F0"),
            rows);
    }

    private static ScanRowDto ToRow(ScanCandidateResult r)
    {
        var rsLayer = r.DecisionResult?.LayerScores.FirstOrDefault(l => l.LayerName == "Relative Strength & Regime");

        return new ScanRowDto(
            r.SymbolId,
            r.NseSymbol,
            r.DecisionResult?.Outcome.ToString() ?? "N/A",
            r.DecisionResult?.ConfidenceScore ?? 0m,
            r.TradePlan?.RiskRewardRatio,
            rsLayer?.ClampedContribution,
            r.ExcludedByDataQualityGate);
    }

    public static DrillDownResponseDto ToDrillDownResponse(ScanCandidateResult r)
    {
        var layerDtos = r.DecisionResult?.LayerScores
            .Select(l => new LayerScoreDto(l.LayerName, l.MaxPoints, l.ClampedContribution))
            .ToList() ?? new List<LayerScoreDto>();

        var gateFailures = r.DecisionResult?.HardGates
            .Where(g => !g.Passed)
            .Select(g => $"{g.Kind}: {g.Reason}")
            .ToList() ?? new List<string>();

        TradePlanDto? tradePlanDto = r.TradePlan is { } tp
            ? new TradePlanDto(tp.Entry, tp.StopLoss, tp.Target1, tp.Target2, tp.Target3, tp.RiskPercent, tp.RiskRewardRatio,
                tp.InvalidationLevel, tp.EstimatedDuration?.ToString(), tp.DurationDataQualityFlag)
            : null;

        return new DrillDownResponseDto(
            r.SymbolId,
            r.NseSymbol,
            r.DecisionResult?.Outcome.ToString() ?? "N/A",
            r.DecisionResult?.ConfidenceScore ?? 0m,
            r.DecisionResult?.ReasoningText ?? "No decision computed — excluded before scoring.",
            layerDtos,
            gateFailures,
            tradePlanDto,
            r.ExcludedByDataQualityGate,
            r.DataQualityFailureReasons.ToList());
    }
}
