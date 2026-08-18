using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Structure;

/// <summary>
/// Detects confirmed swing highs/lows using the N-bars-each-side fractal rule (§8,
/// default 2 bars each side = "5-bar fractal"). Because confirmation requires bars
/// AFTER the candidate bar, a swing point is only ever reported once those trailing
/// bars have closed — this is the concrete mechanism behind §21's "never mark on a
/// forming candle" rule: the candidate bar itself was already closed long before its
/// swing status could even be evaluated.
///
/// Runs as an IBarProcessor so its output (SwingPoint list, including the new-HH/HL/
/// LH/LL flag) is available via IProcessingContext to StructureBreakDetector, which
/// must run at a higher Priority in the same pipeline pass.
/// </summary>
public sealed class SwingPointDetector : IBarProcessor
{
    private readonly int _symbolId;
    private readonly Timeframe _timeframe;
    private readonly int _fractalBars;

    // Buffer must hold at least (2*fractalBars + 1) candles to evaluate the center candle.
    private readonly List<Candle> _window;
    private readonly List<SwingPoint> _confirmedSwings = new();

    public SwingPointDetector(int symbolId, Timeframe timeframe, StructureThresholds thresholds)
    {
        _symbolId = symbolId;
        _timeframe = timeframe;
        _fractalBars = thresholds.SwingFractalBars;
        _window = new List<Candle>(_fractalBars * 2 + 1);
    }

    public int Priority => -200; // must run before StructureBreakDetector

    /// <summary>All confirmed swings detected so far, oldest first.</summary>
    public IReadOnlyList<SwingPoint> ConfirmedSwings => _confirmedSwings;

    public Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct)
    {
        _window.Add(bar);
        if (_window.Count > _fractalBars * 2 + 1)
            _window.RemoveAt(0);

        SwingPoint? newSwing = null;

        if (_window.Count == _fractalBars * 2 + 1)
        {
            var centerIndex = _fractalBars;
            var center = _window[centerIndex];

            var isSwingHigh = true;
            var isSwingLow = true;
            for (var i = 0; i < _window.Count; i++)
            {
                if (i == centerIndex) continue;
                if (_window[i].High >= center.High) isSwingHigh = false;
                if (_window[i].Low <= center.Low) isSwingLow = false;
            }

            // A single candle cannot be confirmed as both — if the fractal condition
            // somehow allows both (extremely tight/flat data), prefer neither rather
            // than emit an ambiguous double-signal; this is a defensive edge case.
            if (isSwingHigh && !isSwingLow)
            {
                newSwing = ConfirmSwing(center, SwingType.High);
            }
            else if (isSwingLow && !isSwingHigh)
            {
                newSwing = ConfirmSwing(center, SwingType.Low);
            }
        }

        ctx.Set("Structure.NewSwing", newSwing);
        ctx.Set("Structure.AllSwings", (IReadOnlyList<SwingPoint>)_confirmedSwings);

        return Task.CompletedTask;
    }

    private SwingPoint ConfirmSwing(Candle center, SwingType type)
    {
        var priorSameType = _confirmedSwings.LastOrDefault(s => s.Type == type);
        var isHigherOrLower = priorSameType is not null &&
            (type == SwingType.High ? center.High > priorSameType.Price : center.Low < priorSameType.Price);

        var swing = new SwingPoint(
            _symbolId,
            _timeframe,
            center.Timestamp,
            type,
            type == SwingType.High ? center.High : center.Low,
            IsBroken: false,
            IsHigherOrLower: isHigherOrLower);

        _confirmedSwings.Add(swing);
        return swing;
    }
}
