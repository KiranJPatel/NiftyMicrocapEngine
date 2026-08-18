using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application;

/// <summary>
/// Read/write context shared by all IBarProcessor instances during one pass over a
/// newly-closed bar. Earlier (lower-Priority) processors write their output here;
/// later processors in the same pass read it — this is how ATR feeds SuperTrend,
/// structure events feed the decision engine, etc, without a circular dependency
/// between the processor classes themselves.
/// </summary>
public interface IProcessingContext
{
    void Set<T>(string key, T value);
    T? Get<T>(string key);
    bool TryGet<T>(string key, out T? value);
}

/// <summary>
/// One participant in the bar-processing pipeline. Breaks circular dependencies
/// between indicators/engines that depend on each other's output for the same
/// closed bar. Matches build spec §3.2 exactly.
/// </summary>
public interface IBarProcessor
{
    /// <summary>Lower runs first — e.g. ATR (low Priority) must run before SuperTrend (which consumes it).</summary>
    int Priority { get; }

    Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct);
}

/// <summary>
/// In-memory implementation of IProcessingContext — one instance per bar-close pass,
/// discarded after. Not persisted; processors that need their output stored are
/// responsible for writing to a repository themselves (typically the last-priority
/// processor in a pass, or a dedicated persistence processor).
/// </summary>
public sealed class ProcessingContext : IProcessingContext
{
    private readonly Dictionary<string, object?> _values = new();

    public void Set<T>(string key, T value) => _values[key] = value;

    public T? Get<T>(string key)
    {
        if (_values.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return default;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        if (_values.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }
}

/// <summary>
/// Runs all registered IBarProcessor instances for a newly-closed bar, in Priority
/// order (ascending). This is where the no-repaint rule (§21) is enforced
/// architecturally: RunAsync's only entry point is a Candle the caller asserts is
/// confirmed-closed — the pipeline has no notion of a "forming" candle at all, so
/// there's no code path by which an in-progress bar's values could leak through.
/// </summary>
public sealed class BarProcessingPipeline
{
    private readonly IReadOnlyList<IBarProcessor> _processors;

    public BarProcessingPipeline(IEnumerable<IBarProcessor> processors)
    {
        _processors = processors.OrderBy(p => p.Priority).ToList();
    }

    public async Task<IProcessingContext> RunAsync(Candle closedBar, CancellationToken ct = default)
    {
        var ctx = new ProcessingContext();
        foreach (var processor in _processors)
        {
            ct.ThrowIfCancellationRequested();
            await processor.OnBarClosedAsync(closedBar, ctx, ct);
        }
        return ctx;
    }
}
