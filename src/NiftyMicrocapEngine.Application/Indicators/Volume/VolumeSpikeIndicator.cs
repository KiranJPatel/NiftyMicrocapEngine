using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Indicators.Volume;

/// <summary>
/// Volume Spike Detection: flags a bar whose Volume exceeds `spikeMultiple` times the
/// trailing Volume SMA (default 2x, matching the "Volume absorption" threshold used
/// in the SMC approximation layer, §9). Reads VolumeSmaIndicator's output from
/// IProcessingContext rather than duplicating the rolling-average calculation —
/// requires VolumeSmaIndicator to be registered in the same pipeline at a lower
/// Priority (it is: -50 vs this indicator's default of 0).
/// </summary>
public sealed class VolumeSpikeIndicator : IndicatorBase
{
    private readonly string _volumeSmaContextKey;
    private readonly decimal _spikeMultiple;
    private int _barsSeen;

    public VolumeSpikeIndicator(int volumeSmaPeriod = 20, decimal spikeMultiple = 2m)
    {
        _volumeSmaContextKey = $"VolumeSMA_{volumeSmaPeriod}";
        _spikeMultiple = spikeMultiple;
        VolumeSmaPeriod = volumeSmaPeriod;
    }

    public int VolumeSmaPeriod { get; }

    /// <summary>1 = exactly at the spike threshold, 0 = at/below the SMA baseline. Null when SMA isn't yet warmed up.</summary>
    public decimal? SpikeRatio { get; private set; }

    public override string Key => $"VolumeSpike_{VolumeSmaPeriod}_{_spikeMultiple}";
    public override int WarmupPeriod => VolumeSmaPeriod;
    public override int Priority => 0; // after VolumeSmaIndicator (-50)

    protected override IndicatorComputation Compute(Candle bar, IProcessingContext ctx, int barsProcessedSoFar)
    {
        _barsSeen++;

        if (!ctx.TryGet<decimal?>(_volumeSmaContextKey, out var volumeSmaNullable) || volumeSmaNullable is not { } volumeSma || volumeSma == 0)
        {
            SpikeRatio = null;
            return new IndicatorComputation(null, "Neutral", 0m, IndicatorHealth.InsufficientData);
        }

        var ratio = bar.Volume / volumeSma;
        SpikeRatio = ratio;

        var isSpike = ratio >= _spikeMultiple;
        var signal = isSpike ? "VolumeSpike" : "Normal";
        var health = _barsSeen < WarmupPeriod ? IndicatorHealth.InsufficientData : IndicatorHealth.OK;

        return new IndicatorComputation(ratio, signal, health == IndicatorHealth.OK ? 1m : 0m, health);
    }
}
