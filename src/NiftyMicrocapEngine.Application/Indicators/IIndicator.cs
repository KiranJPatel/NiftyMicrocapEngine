using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators;

/// <summary>
/// One technical indicator's live state. Matches build spec §7 exactly. Every
/// indicator maintains its own rolling state (typically CircularBuffer-backed) and
/// updates via the IBarProcessor pipeline (§3.2) so cross-indicator dependencies
/// (ATR → SuperTrend) resolve through Priority ordering rather than direct coupling.
/// </summary>
public interface IIndicator
{
    string Key { get; }
    int WarmupPeriod { get; }
    decimal? CurrentValue { get; }
    IReadOnlyList<decimal?> HistoricalValues { get; }

    /// <summary>e.g. "Bullish", "Bearish", "Neutral" — indicator-specific vocabulary, documented per implementation.</summary>
    string SignalState { get; }

    /// <summary>0-1: the indicator's own certainty given warmup/data quality (not the same as Decision Engine confidence, §14).</summary>
    decimal Confidence { get; }

    IndicatorHealth Health { get; }
}

/// <summary>
/// Base class most concrete indicators derive from: wires HistoricalValues tracking
/// and IBarProcessor participation so individual indicators only need to implement
/// ComputeValue. Not mandatory — IIndicator can be implemented directly — but keeps
/// the Phase-2 core set (§7) consistent.
/// </summary>
public abstract class IndicatorBase : IIndicator, IBarProcessor
{
    private readonly List<decimal?> _historicalValues = new();

    public abstract string Key { get; }
    public abstract int WarmupPeriod { get; }
    public abstract int Priority { get; }

    public decimal? CurrentValue { get; private set; }
    public IReadOnlyList<decimal?> HistoricalValues => _historicalValues;
    public string SignalState { get; private set; } = "Neutral";
    public decimal Confidence { get; private set; }
    public IndicatorHealth Health { get; private set; } = IndicatorHealth.InsufficientData;

    /// <summary>
    /// Computes this indicator's value for the newly-closed bar, given the shared
    /// processing context (for reading upstream indicators this one depends on) and
    /// how many bars have been processed so far (for warmup gating).
    /// </summary>
    protected abstract IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar);

    public Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct)
    {
        var barsProcessedSoFar = _historicalValues.Count;
        var computation = Compute(bar, ctx, barsProcessedSoFar);

        CurrentValue = computation.Value;
        SignalState = computation.SignalState;
        Confidence = computation.Confidence;
        Health = barsProcessedSoFar + 1 < WarmupPeriod ? IndicatorHealth.InsufficientData : computation.Health;

        _historicalValues.Add(CurrentValue);
        ctx.Set(Key, CurrentValue);

        return Task.CompletedTask;
    }
}

public sealed record IndicatorComputation(decimal? Value, string SignalState, decimal Confidence, IndicatorHealth Health);
