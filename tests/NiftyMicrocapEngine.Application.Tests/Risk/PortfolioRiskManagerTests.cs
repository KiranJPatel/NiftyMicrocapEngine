using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.Risk;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Risk;

public class PortfolioRiskManagerTests
{
    private static PortfolioRiskManager BuildManager(RiskManagerOptions? options = null) =>
        new(Options.Create(options ?? new RiskManagerOptions
        {
            MaxConcurrentPositions = 10,
            MaxSectorConcentrationPercent = 25m,
            MaxCorrelatedPositions = 3,
            CorrelationThreshold = 0.7m
        }));

    [Fact]
    public void CheckLimits_UnderAllLimits_ReturnsWithinLimits()
    {
        var manager = BuildManager();
        var request = new PortfolioLimitCheckRequest(
            new OpenPosition(1, "IT", 10000m, new[] { 1m, 2m, -1m, 3m }),
            Array.Empty<OpenPosition>(),
            0m);

        var result = manager.CheckLimits(request);

        Assert.True(result.WithinLimits);
        Assert.Empty(result.BreachedLimits);
    }

    [Fact]
    public void CheckLimits_AtMaxConcurrentPositions_BreachesLimit()
    {
        var options = new RiskManagerOptions { MaxConcurrentPositions = 2, MaxSectorConcentrationPercent = 100m, MaxCorrelatedPositions = 100, CorrelationThreshold = 0.99m };
        var manager = BuildManager(options);

        var existing = new[]
        {
            new OpenPosition(1, "IT", 10000m, new[] { 1m, 2m }),
            new OpenPosition(2, "Pharma", 10000m, new[] { 1m, 2m })
        };
        var candidate = new OpenPosition(3, "Auto", 10000m, new[] { 1m, 2m });

        var result = manager.CheckLimits(new PortfolioLimitCheckRequest(candidate, existing, 20000m));

        Assert.False(result.WithinLimits);
        Assert.Contains(result.BreachedLimits, b => b.Contains("concurrent positions"));
    }

    [Fact]
    public void CheckLimits_SectorConcentrationExceeded_BreachesLimit()
    {
        var options = new RiskManagerOptions { MaxConcurrentPositions = 100, MaxSectorConcentrationPercent = 25m, MaxCorrelatedPositions = 100, CorrelationThreshold = 0.99m };
        var manager = BuildManager(options);

        var existing = new[] { new OpenPosition(1, "IT", 20000m, new[] { 1m, 2m }) };
        var candidate = new OpenPosition(2, "IT", 10000m, new[] { 1m, 2m });

        var result = manager.CheckLimits(new PortfolioLimitCheckRequest(candidate, existing, 20000m));

        Assert.False(result.WithinLimits);
        Assert.Contains(result.BreachedLimits, b => b.Contains("sector concentration"));
    }

    [Fact]
    public void CheckLimits_SectorConcentrationWithinLimit_DoesNotBreach()
    {
        var options = new RiskManagerOptions { MaxConcurrentPositions = 100, MaxSectorConcentrationPercent = 50m, MaxCorrelatedPositions = 100, CorrelationThreshold = 0.99m };
        var manager = BuildManager(options);

        var existing = new[] { new OpenPosition(1, "IT", 1000m, new[] { 1m, 2m }) };
        var candidate = new OpenPosition(2, "Pharma", 1000m, new[] { 1m, 2m });

        var result = manager.CheckLimits(new PortfolioLimitCheckRequest(candidate, existing, 2000m));

        Assert.True(result.WithinLimits);
    }

    [Fact]
    public void CheckLimits_HighlyCorrelatedPositionsExceedMax_BreachesLimit()
    {
        var options = new RiskManagerOptions { MaxConcurrentPositions = 100, MaxSectorConcentrationPercent = 100m, MaxCorrelatedPositions = 2, CorrelationThreshold = 0.7m };
        var manager = BuildManager(options);

        var returns = new decimal[] { 1m, 2m, -1m, 3m, 0.5m };
        var existingOne = new[] { new OpenPosition(1, "IT", 1000m, returns) };
        var candidate = new OpenPosition(2, "IT", 1000m, returns);

        var resultOk = manager.CheckLimits(new PortfolioLimitCheckRequest(candidate, existingOne, 1000m));
        Assert.True(resultOk.WithinLimits);

        var existingTwo = new[]
        {
            new OpenPosition(1, "IT", 1000m, returns),
            new OpenPosition(3, "IT", 1000m, returns)
        };
        var resultBreach = manager.CheckLimits(new PortfolioLimitCheckRequest(candidate, existingTwo, 2000m));
        Assert.False(resultBreach.WithinLimits);
        Assert.Contains(resultBreach.BreachedLimits, b => b.Contains("correlated exposure"));
    }

    [Fact]
    public void CheckLimits_UncorrelatedPositions_DoNotCountTowardCorrelationLimit()
    {
        var options = new RiskManagerOptions { MaxConcurrentPositions = 100, MaxSectorConcentrationPercent = 100m, MaxCorrelatedPositions = 1, CorrelationThreshold = 0.7m };
        var manager = BuildManager(options);

        var existing = new[] { new OpenPosition(1, "IT", 1000m, new decimal[] { 1m, 2m, 3m, 4m }) };
        var candidate = new OpenPosition(2, "IT", 1000m, new decimal[] { 4m, 3m, 2m, 1m });

        var result = manager.CheckLimits(new PortfolioLimitCheckRequest(candidate, existing, 1000m));

        Assert.True(result.WithinLimits);
    }

    [Fact]
    public void CheckLimits_MultipleBreaches_AllReported()
    {
        var options = new RiskManagerOptions { MaxConcurrentPositions = 0, MaxSectorConcentrationPercent = 0m, MaxCorrelatedPositions = 100, CorrelationThreshold = 0.99m };
        var manager = BuildManager(options);

        var candidate = new OpenPosition(1, "IT", 1000m, new[] { 1m, 2m });
        var result = manager.CheckLimits(new PortfolioLimitCheckRequest(candidate, Array.Empty<OpenPosition>(), 0m));

        Assert.False(result.WithinLimits);
        Assert.True(result.BreachedLimits.Count >= 2);
    }
}
