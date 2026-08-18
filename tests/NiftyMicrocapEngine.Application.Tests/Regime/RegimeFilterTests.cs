using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.Regime;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Regime;

public class RegimeFilterTests
{
    private static RegimeFilter BuildFilter(decimal overrideConfidence = 90m)
    {
        var options = new DecisionEngineOptions { RegimeOverrideConfidence = overrideConfidence };
        return new RegimeFilter(Options.Create(options));
    }

    [Theory]
    [InlineData(BroadMarketTrendState.Bear)]
    [InlineData(BroadMarketTrendState.StrongBear)]
    public void Evaluate_BearOrStrongBearNifty50_SuppressesNewLongs(BroadMarketTrendState trend)
    {
        var filter = BuildFilter();
        var marketState = new BroadMarketState(trend, BroadMarketTrendState.Neutral, new DateOnly(2026, 1, 15));

        var result = filter.Evaluate(marketState, TrendDirection.Bullish);

        Assert.True(result.IsSuppressed);
        Assert.Equal(90m, result.RequiredOverrideConfidence);
    }

    [Theory]
    [InlineData(BroadMarketTrendState.Neutral)]
    [InlineData(BroadMarketTrendState.Bull)]
    [InlineData(BroadMarketTrendState.StrongBull)]
    public void Evaluate_NonBearishNifty50_DoesNotSuppress(BroadMarketTrendState trend)
    {
        var filter = BuildFilter();
        var marketState = new BroadMarketState(trend, BroadMarketTrendState.Neutral, new DateOnly(2026, 1, 15));

        var result = filter.Evaluate(marketState, TrendDirection.Bullish);

        Assert.False(result.IsSuppressed);
    }

    [Fact]
    public void Evaluate_BearMarket_DoesNotSuppressShorts()
    {
        // The suppression rule is specific to "new long signals" — shorts should
        // pass through unaffected by bearish regime.
        var filter = BuildFilter();
        var marketState = new BroadMarketState(BroadMarketTrendState.StrongBear, BroadMarketTrendState.Bear, new DateOnly(2026, 1, 15));

        var result = filter.Evaluate(marketState, TrendDirection.Bearish);

        Assert.False(result.IsSuppressed);
    }

    [Fact]
    public void Evaluate_UsesConfiguredOverrideThreshold_NotHardcoded()
    {
        var filter = BuildFilter(overrideConfidence: 95m);
        var marketState = new BroadMarketState(BroadMarketTrendState.Bear, BroadMarketTrendState.Neutral, new DateOnly(2026, 1, 15));

        var result = filter.Evaluate(marketState, TrendDirection.Bullish);

        Assert.Equal(95m, result.RequiredOverrideConfidence);
    }
}
