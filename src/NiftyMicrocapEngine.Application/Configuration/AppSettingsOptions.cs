namespace NiftyMicrocapEngine.Application.Configuration;

public sealed class DataProvidersOptions
{
    public const string SectionName = "DataProviders";
    public YahooProviderOptions Yahoo { get; init; } = new();
    public BrokerProviderOptions Broker { get; init; } = new();
}

public sealed class YahooProviderOptions
{
    public int RequestsPerSecond { get; init; } = 2;
    public int TimeoutSeconds { get; init; } = 15;
    public int RetryCount { get; init; } = 3;

    /// <summary>Not in §19's snippet but required to actually reach the endpoint — verify at Phase 0.</summary>
    public string BaseUrl { get; init; } = "https://query1.finance.yahoo.com";
}

public sealed class BrokerProviderOptions
{
    public string PreferredBroker { get; init; } = "Zerodha";
    public int TimeoutSeconds { get; init; } = 15;
    public int RetryCount { get; init; } = 3;
}

public sealed class DataProviderRoutingOptions
{
    public const string SectionName = "DataProviderRouting";
    public string Daily { get; init; } = "Yahoo";
    public string Weekly { get; init; } = "Yahoo";
    public string H1 { get; init; } = "Broker";
    public string M30 { get; init; } = "Broker";
    public string M15 { get; init; } = "Broker";
    public bool FallbackToYahooOnBrokerFailure { get; init; } = true;
}

public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";
    public int LookbackDays { get; init; } = 90;
    public decimal AdjCloseToleranceFraction { get; init; } = 0.001m;

    /// <summary>
    /// Closes the §24 Phase 7 gap flagged in the README's "Still open" list:
    /// nothing scheduled the reconciliation job, so it only ran when someone
    /// manually invoked the CLI command or dashboard endpoint. Not in §19's
    /// original config snippet — added the same way ScannerOptions'
    /// MaxDegreeOfParallelism was, for a real operational need the spec
    /// described (§6.6: "scheduled job") without pinning a concrete schedule.
    /// ScheduledEnabled defaults false: a hosted service silently hitting
    /// Yahoo on a schedule the operator didn't explicitly turn on is worse
    /// than one that requires an explicit opt-in.
    /// </summary>
    public bool ScheduledEnabled { get; init; } = false;

    /// <summary>Hour of day, 0-23, in IST (see IndiaStandardTime), the scheduled job targets. Default 20 (8 PM IST) — well after NSE's 3:30 PM close and Yahoo's own end-of-day AdjClose settling.</summary>
    public int ScheduledHourIst { get; init; } = 20;
}

public sealed class DataQualityGateOptions
{
    public const string SectionName = "DataQualityGate";
    public int TrailingWindowDays { get; init; } = 60;
    public int MinimumNonZeroVolumeDays { get; init; } = 30;
    public int MaxConsecutiveNoTradeDays { get; init; } = 10;
}

public sealed class MultiTimeframeOptions
{
    public const string SectionName = "MultiTimeframe";
    public MultiTimeframeWeights Weights { get; init; } = new();
}

public sealed class MultiTimeframeWeights
{
    public decimal Weekly { get; init; } = 40m;
    public decimal Daily { get; init; } = 35m;
    public decimal H1 { get; init; } = 10m;
    public decimal M30 { get; init; } = 8m;
    public decimal M15 { get; init; } = 7m;
}

public sealed class DecisionEngineOptions
{
    public const string SectionName = "DecisionEngine";
    public DecisionLayerWeights LayerWeights { get; init; } = new();
    public DecisionThresholds Thresholds { get; init; } = new();

    /// <summary>Weighted-score threshold required to override an active regime-suppression gate — see the audited §13/§14 short-circuit fix.</summary>
    public decimal RegimeOverrideConfidence { get; init; } = 90m;
}

public sealed class DecisionLayerWeights
{
    public decimal Structure { get; init; } = 25m;
    public decimal Trend { get; init; } = 20m;
    public decimal Momentum { get; init; } = 15m;
    public decimal Volume { get; init; } = 15m;
    public decimal Volatility { get; init; } = 10m;
    public decimal Psychology { get; init; } = 5m;
    public decimal SupportResistance { get; init; } = 5m;
    public decimal RelativeStrengthRegime { get; init; } = 5m;
}

public sealed class DecisionThresholds
{
    public decimal StrongBuy { get; init; } = 80m;
    public decimal Buy { get; init; } = 65m;
    public decimal Watch { get; init; } = 50m;
    public decimal Hold { get; init; } = 35m;
    public decimal Sell { get; init; } = 20m;
}

public sealed class RelativeStrengthOptions
{
    public const string SectionName = "RelativeStrength";
    public int LookbackDaysShort { get; init; } = 20;
    public int LookbackDaysLong { get; init; } = 60;
}

public sealed class RiskManagerOptions
{
    public const string SectionName = "RiskManager";
    public decimal RiskPerTradePercent { get; init; } = 0.5m;
    public int MaxConcurrentPositions { get; init; } = 10;
    public decimal MaxSectorConcentrationPercent { get; init; } = 25m;
    public int MaxCorrelatedPositions { get; init; } = 3;
    public decimal CorrelationThreshold { get; init; } = 0.7m;
    public decimal StopAtrMultiple { get; init; } = 1.5m;
}

public sealed class ScannerOptions
{
    public const string SectionName = "Scanner";
    public int Stage2ShortlistSize { get; init; } = 30;

    /// <summary>
    /// Bounded concurrency for Stage 1's per-symbol scan loop. Not in §19's
    /// original config snippet — added because Stage 1 was originally
    /// sequential and section 17's "well under 5 minutes for all 250
    /// symbols" target is not realistically achievable sequentially once
    /// live network fetches are involved (rather than pure cache hits).
    /// Default of 8 balances real throughput against not overwhelming the
    /// rate-limited Yahoo/NSE HttpClients (see Infrastructure.YahooFinance's
    /// RateLimitingHandler, which enforces DataProviders:Yahoo:RequestsPerSecond
    /// independently of this setting — the two work together, not against
    /// each other: this bounds concurrent SYMBOLS in flight, the rate
    /// limiter bounds concurrent REQUESTS per second across all of them).
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 8;

    /// <summary>
    /// Closes the last unscheduled piece of Phase 7's automation gap (the
    /// scan itself — reconciliation already has ReconciliationOptions'
    /// equivalent pair). Same opt-in-by-default reasoning: a hosted service
    /// silently running a live scan (which hits Yahoo/NSE for every
    /// universe symbol) on an implicit schedule is worse than one requiring
    /// an explicit flip in appsettings.
    /// </summary>
    public bool ScheduledEnabled { get; init; } = false;

    /// <summary>Hour of day, 0-23, in IST, the scheduled scan targets. Default 16 (4 PM IST) — after NSE's 3:30 PM close, before Reconciliation's default 8 PM slot, so a scheduled scan sees the day's final candle and reconciliation (if also scheduled) still runs after it with margin.</summary>
    public int ScheduledHourIst { get; init; } = 16;
}

public sealed class NseIndicesOptions
{
    public const string SectionName = "NseIndices";

    /// <summary>Not explicitly named in §19's config snippet but required to actually reach the endpoint — verify at Phase 0. See §6.5.</summary>
    public string BaseUrl { get; init; } = "https://nsearchives.nseindia.com";
}

/// <summary>
/// Provider-specific symbols for the two benchmark indices the Regime Filter
/// (§13) and Relative Strength calculator need. Not explicitly named as a
/// config section anywhere in §19's snippet — added here because the
/// Scanner needs a concrete symbol to fetch, and Yahoo's actual index ticker
/// conventions (^NSEI, ^CRSMID etc.) are exactly the kind of "verify at
/// implementation time" detail §6.2 flags for equity symbols too. Defaults
/// reflect Yahoo's documented index-symbol conventions as of this build's
/// last verification — confirm against a live Yahoo request (see the Phase 0
/// smoke test) before relying on them, same as any other Yahoo symbol here.
/// </summary>
public sealed class BenchmarkIndicesOptions
{
    public const string SectionName = "BenchmarkIndices";

    /// <summary>Yahoo Finance symbol for the Nifty 50 index.</summary>
    public string Nifty50YahooSymbol { get; init; } = "^NSEI";

    /// <summary>
    /// Yahoo Finance symbol for a Nifty Midcap index, used by the Regime
    /// Filter's secondary broad-market read. §13 references "Nifty Midcap"
    /// without specifying which exact midcap index variant — Nifty Midcap 100
    /// is the commonly quoted one; verify against what's actually available
    /// via Yahoo at implementation time.
    /// </summary>
    public string NiftyMidcapYahooSymbol { get; init; } = "^CRSMID";

    /// <summary>
    /// Yahoo Finance symbol for the Nifty Microcap 250 index, used by the
    /// Relative Strength calculator. §13 explicitly names this index as the
    /// primary relative-strength benchmark for the engine's own universe.
    /// </summary>
    public string NiftyMicrocap250YahooSymbol { get; init; } = "NIFTY_MICROCAP250.NS";
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public string SqliteConnectionString { get; init; } = "Data Source=niftymicrocapengine.db";
}
