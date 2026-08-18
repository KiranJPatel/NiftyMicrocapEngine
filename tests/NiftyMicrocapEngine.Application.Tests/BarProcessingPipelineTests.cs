using NiftyMicrocapEngine.Application;
using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Application.Tests;

public class BarProcessingPipelineTests
{
    private static Candle Candle(int day, decimal close) => new(
        SymbolId: 1, Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
        Open: close, High: close + 1, Low: close - 1, Close: close, AdjClose: close, Volume: 1000);

    [Fact]
    public async Task RunAsync_RunsProcessorsInPriorityOrder()
    {
        var executionOrder = new List<string>();
        var high = new RecordingProcessor("high", priority: 10, executionOrder);
        var low = new RecordingProcessor("low", priority: -10, executionOrder);
        var mid = new RecordingProcessor("mid", priority: 0, executionOrder);

        var pipeline = new BarProcessingPipeline(new IBarProcessor[] { high, mid, low });
        await pipeline.RunAsync(Candle(1, 100m));

        Assert.Equal(new[] { "low", "mid", "high" }, executionOrder);
    }

    [Fact]
    public async Task RunAsync_LaterProcessorCanReadEarlierProcessorsContextWrite()
    {
        var writer = new ContextWritingProcessor(priority: -100, key: "Upstream", value: 42m);
        var reader = new ContextReadingProcessor(priority: 0, key: "Upstream");

        var pipeline = new BarProcessingPipeline(new IBarProcessor[] { reader, writer });
        var ctx = await pipeline.RunAsync(Candle(1, 100m));

        Assert.Equal(42m, reader.ObservedValue);
    }

    [Fact]
    public async Task RunAsync_WhenUpstreamProcessorNotRegistered_DownstreamSeesNoValue()
    {
        var reader = new ContextReadingProcessor(priority: 0, key: "NeverWritten");
        var pipeline = new BarProcessingPipeline(new IBarProcessor[] { reader });

        await pipeline.RunAsync(Candle(1, 100m));

        Assert.Null(reader.ObservedValue);
    }

    private sealed class RecordingProcessor : IBarProcessor
    {
        private readonly string _name;
        private readonly List<string> _executionOrder;
        public int Priority { get; }

        public RecordingProcessor(string name, int priority, List<string> executionOrder)
        {
            _name = name;
            Priority = priority;
            _executionOrder = executionOrder;
        }

        public Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct)
        {
            _executionOrder.Add(_name);
            return Task.CompletedTask;
        }
    }

    private sealed class ContextWritingProcessor : IBarProcessor
    {
        private readonly string _key;
        private readonly decimal _value;
        public int Priority { get; }

        public ContextWritingProcessor(int priority, string key, decimal value)
        {
            Priority = priority;
            _key = key;
            _value = value;
        }

        public Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct)
        {
            ctx.Set(_key, _value);
            return Task.CompletedTask;
        }
    }

    private sealed class ContextReadingProcessor : IBarProcessor
    {
        private readonly string _key;
        public int Priority { get; }
        public decimal? ObservedValue { get; private set; }

        public ContextReadingProcessor(int priority, string key)
        {
            Priority = priority;
            _key = key;
        }

        public Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct)
        {
            ObservedValue = ctx.TryGet<decimal?>(_key, out var v) ? v : null;
            return Task.CompletedTask;
        }
    }
}
