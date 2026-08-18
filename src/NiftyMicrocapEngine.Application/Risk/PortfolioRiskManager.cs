using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;

namespace NiftyMicrocapEngine.Application.Risk;

/// <summary>
/// Implements build spec section 16.2's portfolio-level limits: max concurrent
/// positions, max sector concentration, and max correlated exposure (no more
/// than 3 concurrent positions with pairwise 60-day return correlation above
/// the configured threshold — a simple pairwise check, not full portfolio
/// optimization, per the spec's explicit scope limitation).
/// </summary>
public sealed class PortfolioRiskManager : IPortfolioRiskManager
{
    private readonly RiskManagerOptions _options;

    public PortfolioRiskManager(IOptions<RiskManagerOptions> options)
    {
        _options = options.Value;
    }

    public PortfolioLimitCheckResult CheckLimits(PortfolioLimitCheckRequest request)
    {
        var breaches = new List<string>();

        var positionCountIfAdded = request.ExistingOpenPositions.Count + 1;
        if (positionCountIfAdded > _options.MaxConcurrentPositions)
        {
            breaches.Add(
                $"Max concurrent positions ({_options.MaxConcurrentPositions}) would be exceeded: " +
                $"{request.ExistingOpenPositions.Count} open + this candidate = {positionCountIfAdded}.");
        }

        var sectorDeployedIfAdded = request.ExistingOpenPositions
            .Where(p => p.Sector == request.CandidatePosition.Sector)
            .Sum(p => p.DeployedCapital) + request.CandidatePosition.DeployedCapital;

        var totalDeployedIfAdded = request.TotalDeployedCapital + request.CandidatePosition.DeployedCapital;
        var sectorConcentrationPercent = totalDeployedIfAdded == 0 ? 0m : sectorDeployedIfAdded / totalDeployedIfAdded * 100m;

        if (sectorConcentrationPercent > _options.MaxSectorConcentrationPercent)
        {
            breaches.Add(
                $"Max sector concentration ({_options.MaxSectorConcentrationPercent}%) would be exceeded in sector " +
                $"'{request.CandidatePosition.Sector}': {sectorConcentrationPercent:F1}% after adding this candidate.");
        }

        var correlatedCount = CountCorrelatedPositions(request.CandidatePosition, request.ExistingOpenPositions, _options.CorrelationThreshold);
        if (correlatedCount + 1 > _options.MaxCorrelatedPositions)
        {
            breaches.Add(
                $"Max correlated exposure ({_options.MaxCorrelatedPositions} positions with pairwise 60-day return " +
                $"correlation > {_options.CorrelationThreshold}) would be exceeded: candidate is correlated with " +
                $"{correlatedCount} existing open position(s).");
        }

        return new PortfolioLimitCheckResult(breaches.Count == 0, breaches);
    }

    private static int CountCorrelatedPositions(OpenPosition candidate, IReadOnlyList<OpenPosition> existing, decimal threshold)
    {
        return existing.Count(p => PearsonCorrelation(candidate.Trailing60DayReturns, p.Trailing60DayReturns) is { } corr && corr > threshold);
    }

    /// <summary>
    /// Standard Pearson correlation coefficient between two equal-length return
    /// series. Returns null (not a fabricated 0 or 1) if either series has
    /// fewer than 2 points, has zero variance, or the series lengths differ —
    /// an undefined correlation is reported as such, not silently treated as
    /// "uncorrelated" (which would wrongly be treated as safe to add).
    /// </summary>
    private static decimal? PearsonCorrelation(IReadOnlyList<decimal> a, IReadOnlyList<decimal> b)
    {
        if (a.Count != b.Count || a.Count < 2) return null;

        var n = a.Count;
        var meanA = a.Average();
        var meanB = b.Average();

        decimal covariance = 0m, varianceA = 0m, varianceB = 0m;

        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            covariance += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }

        if (varianceA == 0 || varianceB == 0) return null;

        var denominator = (decimal)Math.Sqrt((double)(varianceA * varianceB));
        return denominator == 0 ? null : covariance / denominator;
    }
}
