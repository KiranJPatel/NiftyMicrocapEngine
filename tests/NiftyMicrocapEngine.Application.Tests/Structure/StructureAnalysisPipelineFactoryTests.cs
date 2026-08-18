using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests.Structure;

public class StructureAnalysisPipelineFactoryTests
{
    private static Candle Candle(int day, decimal open, decimal high, decimal low, decimal close, long volume = 1000) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: open, High: high, Low: low, Close: close, AdjClose: close, Volume: volume);

    [Fact]
    public async Task Create_ProducesPipelineThatRunsWithoutThrowing_AndPopulatesAllHandles()
    {
        var handles = StructureAnalysisPipelineFactory.Create(symbolId: 1, Timeframe.Daily);

        var rnd = new Random(7);
        var price = 100m;

        for (var day = 1; day <= 60; day++)
        {
            var wiggle = (decimal)(rnd.NextDouble() * 3 - 1.5);
            var close = price + wiggle;
            var high = Math.Max(price, close) + 0.5m;
            var low = Math.Min(price, close) - 0.5m;
            var candle = Candle(day, price, high, low, close, volume: 1000 + rnd.Next(-200, 200));

            await handles.Pipeline.RunAsync(candle);
            price = close;
        }

        Assert.NotNull(handles.Atr.CurrentValue);
        Assert.NotNull(handles.VolumeSma.CurrentValue);
        Assert.NotEmpty(handles.SwingPoints.ConfirmedSwings);
    }

    [Fact]
    public async Task Create_TwoInstancesForDifferentSymbols_DoNotShareState()
    {
        var handlesA = StructureAnalysisPipelineFactory.Create(symbolId: 1, Timeframe.Daily);
        var handlesB = StructureAnalysisPipelineFactory.Create(symbolId: 2, Timeframe.Daily);

        // Feed only handlesA with enough bars to warm up ATR; handlesB gets nothing.
        var price = 100m;
        for (var day = 1; day <= 20; day++)
        {
            var candle = Candle(day, price, price + 1, price - 1, price + 0.1m);
            await handlesA.Pipeline.RunAsync(candle);
            price += 0.1m;
        }

        Assert.NotNull(handlesA.Atr.CurrentValue);
        Assert.Null(handlesB.Atr.CurrentValue); // proves no shared/static state between instances
    }
}
