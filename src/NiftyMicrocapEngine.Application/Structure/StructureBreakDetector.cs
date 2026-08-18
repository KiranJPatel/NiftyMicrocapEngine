using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Structure;

/// <summary>
/// Detects Break of Structure (BOS) and Change of Character (CHoCH) events per §8.
///
/// Rule recap: BOS = a closed candle's Close beyond the most recent unbroken swing
/// point IN the direction of the prevailing trend (continuation). CHoCH = the FIRST
/// BOS in the OPPOSITE direction of the prevailing trend, after a run of same-
/// direction BOS events — i.e. CHoCH is what flips PrevailingTrend; every break
/// thereafter in the new direction is BOS again until the next opposite-direction
/// break, which becomes the next CHoCH.
///
/// Must run at a higher Priority than SwingPointDetector in the same pipeline, since
/// it reads "Structure.NewSwing" / "Structure.AllSwings" from the shared context.
/// </summary>
public sealed class StructureBreakDetector : IBarProcessor
{
    private readonly int _symbolId;
    private readonly Timeframe _timeframe;

    private TrendDirection _prevailingTrend = TrendDirection.Ranging;
    private SwingPoint? _mostRecentUnbrokenHigh;
    private SwingPoint? _mostRecentUnbrokenLow;
    private readonly List<StructureBreakEvent> _breaks = new();

    public StructureBreakDetector(int symbolId, Timeframe timeframe)
    {
        _symbolId = symbolId;
        _timeframe = timeframe;
    }

    public int Priority => -190; // after SwingPointDetector (-200), before anything consuming breaks

    public TrendDirection PrevailingTrend => _prevailingTrend;
    public IReadOnlyList<StructureBreakEvent> Breaks => _breaks;

    public Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct)
    {
        // Track the most recent unbroken swing of each type as new swings confirm.
        if (ctx.TryGet<SwingPoint?>("Structure.NewSwing", out var newSwing) && newSwing is not null)
        {
            if (newSwing.Type == SwingType.High) _mostRecentUnbrokenHigh = newSwing;
            else _mostRecentUnbrokenLow = newSwing;
        }

        StructureBreakEvent? breakEvent = null;

        // A break requires an unbroken swing to break beyond. Close-based (not
        // wick-based) per §8's explicit "closed candle's Close" wording — this is
        // also what distinguishes a confirmed BOS from a Liquidity Grab (§9), which
        // is wick-only and explicitly does NOT close beyond the level.
        if (_mostRecentUnbrokenHigh is { IsBroken: false } upSwing && bar.Close > upSwing.Price)
        {
            breakEvent = RecordBreak(bar, upSwing, TrendDirection.Bullish);
            _mostRecentUnbrokenHigh = upSwing with { IsBroken = true };
        }
        else if (_mostRecentUnbrokenLow is { IsBroken: false } downSwing && bar.Close < downSwing.Price)
        {
            breakEvent = RecordBreak(bar, downSwing, TrendDirection.Bearish);
            _mostRecentUnbrokenLow = downSwing with { IsBroken = true };
        }

        ctx.Set("Structure.NewBreak", breakEvent);
        ctx.Set("Structure.PrevailingTrend", _prevailingTrend);

        return Task.CompletedTask;
    }

    private StructureBreakEvent RecordBreak(Candle bar, SwingPoint brokenSwing, TrendDirection breakDirection)
    {
        // CHoCH = first break opposite the CURRENT prevailing trend. If there is no
        // prevailing trend yet (Ranging, i.e. this is the very first break observed),
        // treat it as establishing the trend via a BOS rather than a CHoCH — there's
        // no prior direction for it to have "changed character" from.
        var isChoch = _prevailingTrend != TrendDirection.Ranging && breakDirection != _prevailingTrend;
        var kind = isChoch ? StructureBreakKind.CHoCH : StructureBreakKind.BOS;

        _prevailingTrend = breakDirection;

        var breakEvent = new StructureBreakEvent(_symbolId, _timeframe, bar.Timestamp, kind, breakDirection, brokenSwing, bar.Close);
        _breaks.Add(breakEvent);
        return breakEvent;
    }
}
