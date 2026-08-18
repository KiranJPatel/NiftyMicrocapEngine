using NiftyMicrocapEngine.Application.Indicators;
using NiftyMicrocapEngine.Application.Indicators.Momentum;
using NiftyMicrocapEngine.Application.Indicators.Trend;
using NiftyMicrocapEngine.Application.Indicators.Volatility;
using NiftyMicrocapEngine.Application.Indicators.Volume;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Structure;

/// <summary>
/// Assembles a complete per-symbol/timeframe BarProcessingPipeline: the Phase-2 core
/// indicator set (build spec §7) plus the full structure engine (§8/§9), all
/// Priority-ordered correctly. This exists so callers (the scanner, backtester,
/// live-update path — all later phases) don't need to know or maintain the specific
/// Priority values each processor requires; get it wired right here once, get it
/// right everywhere downstream.
///
/// One instance of the returned pipeline (and its constituent processors) must be
/// used per symbol+timeframe combination — all state here is per-series, not shared.
/// </summary>
public static class StructureAnalysisPipelineFactory
{
    public sealed record Handles(
        BarProcessingPipeline Pipeline,
        SwingPointDetector SwingPoints,
        StructureBreakDetector StructureBreaks,
        ImpulseLegClassifier ImpulseLegs,
        SmcZoneDetector SmcZones,
        SmcEventDetector SmcEvents,
        AtrIndicator Atr,
        VolumeSmaIndicator VolumeSma,
        IReadOnlyList<IIndicator> AllIndicators)
    {
        /// <summary>
        /// Snapshots CurrentValue for every indicator registered in this pipeline,
        /// keyed by each indicator's Key (e.g. "RSI_14", "EMA_20", "ADX_14").
        ///
        /// FIX (see UniverseScanner.DecisionInput.cs): this is what feeds
        /// DecisionEngineInput.IndicatorValues. Before this fix, the caller built
        /// that dictionary empty on every call — the indicators were computed (they
        /// run as IBarProcessors in this pipeline and their CurrentValue updates
        /// every closed bar) but never read back out, so DecisionEngine.LayersPart1/2
        /// (which key off "EMA_20", "EMA_50", "ADX_14", "RSI_14", "MACD_12_26_9",
        /// "Stochastic_14_3", "OBV", "HistVol_20") always fell through to their
        /// "indicator absent" branch, silently, on every symbol, every run. Call
        /// this once after the pipeline has processed the full candle history for
        /// the as-of date, not per-bar — CurrentValue already reflects the most
        /// recently processed closed bar per the no-repaint policy (§21).
        /// </summary>
        public IReadOnlyDictionary<string, decimal?> SnapshotIndicatorValues() =>
            AllIndicators.ToDictionary(i => i.Key, i => i.CurrentValue);
    }

    public static Handles Create(int symbolId, Timeframe timeframe, StructureThresholds? thresholds = null)
    {
        thresholds ??= new StructureThresholds();

        var atr = new AtrIndicator(thresholds.AtrPeriod);
        var volumeSma = new VolumeSmaIndicator(thresholds.VolumeSmaPeriodForAbsorption);

        var swingPoints = new SwingPointDetector(symbolId, timeframe, thresholds);
        var structureBreaks = new StructureBreakDetector(symbolId, timeframe);
        var impulseLegs = new ImpulseLegClassifier(symbolId, timeframe, thresholds);
        var smcZones = new SmcZoneDetector(symbolId, timeframe);
        var smcEvents = new SmcEventDetector(symbolId, timeframe, thresholds);

        // Remaining Phase-2 core indicators that don't have structure-engine
        // dependencies — included so a single pipeline produces everything needed
        // for the Decision Engine's layers (§14) in one pass per closed candle.
        var stdDev = new StandardDeviationIndicator();
        var bollinger = new BollingerBandsIndicator();
        var rsi = new RsiIndicator();
        var macd = new MacdIndicator();
        var stochastic = new StochasticIndicator();
        var adx = new AdxIndicator();
        var sma20 = new SmaIndicator(20);
        var sma50 = new SmaIndicator(50);
        var ema20 = new EmaIndicator(20);
        var ema50 = new EmaIndicator(50);
        var hma = new HmaIndicator(20);
        var vwma = new VwmaIndicator(20);
        var superTrend = new SuperTrendIndicator(thresholds.AtrPeriod);
        var donchian = new DonchianChannelIndicator();
        var obv = new ObvIndicator();
        var vwap = new VwapIndicator();
        var rollingVwap = new RollingVwapIndicator();
        var volumeSpike = new VolumeSpikeIndicator(thresholds.VolumeSmaPeriodForAbsorption);
        var histVol = new HistoricalVolatilityIndicator();

        // Phase-2 extended set (§7: "pluggable additions, after the core
        // decision engine is validated"). Each is self-contained (computes
        // any internal ATR/RSI/EMA it needs rather than reading another
        // instance's IProcessingContext key), so Priority 0 is correct for
        // all of them — no ordering dependency on the Phase-1 set above. See
        // ExtendedTrendIndicators.cs's doc comment: registering these here
        // makes them computed and persisted (via
        // UniverseScanner.Persistence.cs's now-dynamic snapshot list) but
        // does NOT change what DecisionEngine.LayersPart1/2 reads — that's
        // a deliberately separate decision.
        var wma = new WmaIndicator();
        var dema = new DemaIndicator();
        var tema = new TemaIndicator();
        var kama = new KamaIndicator();
        var ichimoku = new IchimokuIndicator();
        var regressionChannel = new RegressionChannelIndicator();
        var stochasticRsi = new StochasticRsiIndicator();
        var cci = new CciIndicator();
        var roc = new RocIndicator();
        var williamsR = new WilliamsRIndicator();
        var trix = new TrixIndicator();
        var chaikinMoneyFlow = new ChaikinMoneyFlowIndicator();
        var moneyFlowIndex = new MoneyFlowIndexIndicator();
        var volumeEma = new VolumeEmaIndicator();
        var keltnerChannel = new KeltnerChannelIndicator();
        var rangeCompressionExpansion = new RangeCompressionExpansionIndicator();

        var pipeline = new BarProcessingPipeline(new IBarProcessor[]
        {
            atr,
            volumeSma, stdDev,
            swingPoints, structureBreaks, impulseLegs, smcZones, smcEvents,
            sma20, sma50, ema20, ema50, hma, vwma, donchian,
            rsi, macd, stochastic, adx,
            obv, vwap, rollingVwap, histVol,
            superTrend, bollinger, volumeSpike,
            wma, dema, tema, kama, ichimoku, regressionChannel,
            stochasticRsi, cci, roc, williamsR, trix,
            chaikinMoneyFlow, moneyFlowIndex, volumeEma,
            keltnerChannel, rangeCompressionExpansion
        });

        // Every Phase-1 AND Phase-2 indicator (§7) — SnapshotIndicatorValues()
        // needs the full set so DecisionEngineInput.IndicatorValues sees
        // everything DecisionEngine.LayersPart1/2 might key off, present or
        // future, and so persistence (UniverseScanner.Persistence.cs) writes
        // every indicator's latest reading, not just the two the structure
        // engine itself depends on (Atr, VolumeSma).
        var allIndicators = new IIndicator[]
        {
            atr, volumeSma, stdDev, sma20, sma50, ema20, ema50, hma, vwma, donchian,
            rsi, macd, stochastic, adx, obv, vwap, rollingVwap, histVol,
            superTrend, bollinger, volumeSpike,
            wma, dema, tema, kama, ichimoku, regressionChannel,
            stochasticRsi, cci, roc, williamsR, trix,
            chaikinMoneyFlow, moneyFlowIndex, volumeEma,
            keltnerChannel, rangeCompressionExpansion
        };

        return new Handles(pipeline, swingPoints, structureBreaks, impulseLegs, smcZones, smcEvents, atr, volumeSma, allIndicators);
    }
}
