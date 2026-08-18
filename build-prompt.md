# NiftyMicrocapEngine — Complete Build Prompt (Self-Contained, All Phases)

## 0. How to Use This Document

This document is the **sole source of truth** for building NiftyMicrocapEngine end to end. It does not assume you have access to, or knowledge of, any sibling system (referred to below only as prior art that may or may not be present in your workspace). Every component needed is specified here — architecture patterns, algorithms, schemas, configuration, and default parameter values — so the system is buildable from nothing.

**One exception, stated once here and not repeated throughout:** before implementing any component under §3 (Shared Infrastructure) or reimplementing broker connectivity under §6.3, check whether your workspace already contains a working implementation (e.g., in a sibling project directory such as one named `NiftySMC`, `NiftyHATMS`, `NiftyFnOSMC`, or `NiftyEquitySMC`). If a component exists there and does what's specified, **reuse or adapt it directly rather than reimplementing from this spec** — this saves build effort and keeps behavior consistent with whatever else exists in the workspace. If nothing is found, implement fresh exactly per the specifications below. Do not block on searching exhaustively — check once per component, proceed either way.

Build in the phase order given in §24. Each phase has explicit acceptance criteria; don't start a phase until the previous one's criteria are met.

---

## 1. Objective & Scope

Build a C# .NET 8 system that scans the NSE Nifty Microcap 250 universe using only OHLCV historical data and produces Buy/Strong Buy/Watch/Hold/Sell/Strong Sell/Exit/No Trade recommendations with full explainability, the way an experienced discretionary swing trader would reason about a chart — layered context (structure → trend → momentum → volume → volatility → psychology → risk), not indicator voting. No AI/ML models, no external prediction APIs, no paid market data beyond what's specified in §6. This is a **recommendation and analysis engine only** — no order execution, no broker order placement, no live position management. It produces a `TradeSignal` output; execution, if ever wanted, is a separate downstream system consuming that output.

---

## 2. Assumptions & Defaults

Every open question in this system has been resolved to a concrete default below. All defaults are configuration-driven (Options pattern) and can be changed without a code change — treat every number in this table as a starting point to validate via the walk-forward backtester (§16, Phase 6), not a tuned final value.

| # | Assumption | Default selected | Configured in |
|---|---|---|---|
| 1 | Primary analysis timeframes | Daily, Weekly only (Monthly dropped) | `Timeframe` enum, §5.2 |
| 2 | Confirmation timeframes | H1, M30, M15 (rolling window, not primary) | `Timeframe` enum, §5.2 |
| 3 | Excluded timeframes | 1m, 5m — not sustainably backfillable at swing-trading depth | — |
| 4 | Data providers | Two: Yahoo Finance (Daily/Weekly, default) + a broker historical API (H1/M30/M15, preferred) | `DataProviderRouting`, §6.4 |
| 5 | Broker provider identity | Zerodha Kite Connect if credentials/subscription available, else Upstox, else Yahoo-only fallback | `DataProviders:Broker`, §19 |
| 6 | UI technology | Server-rendered/HTML dashboard + charting terminal, dark modern theme | §20 |
| 7 | Charting library | TradingView Lightweight Charts (open-source, OHLC-native) — swap if a better-fit library is already in use elsewhere in the workspace | §20.3 |
| 8 | Structure/SMC rule ownership | Defined explicitly in this spec (§8, §9) using standard SMC/ICT formalizations — not sourced from an external engine | §8, §9 |
| 9 | Swing point detection | 5-bar fractal (2 bars each side) | `StructureThresholds`, §19 |
| 10 | Impulsive move threshold | Range ≥ 1.5× ATR(14), or a move producing a BOS within 3 candles | `StructureThresholds`, §19 |
| 11 | Decision Engine layer weights | Structure 25 / Trend 20 / Momentum 15 / Volume 15 / Volatility 10 / Psychology 5 / S&R 5 / Relative Strength & Regime 5 (sums to 100) | `DecisionEngine:LayerWeights`, §19 |
| 12 | Confidence → decision thresholds | ≥80 Strong Buy, 65–79 Buy, 50–64 Watch, 35–49 Hold, 20–34 Sell, <20 Strong Sell/Exit | `DecisionEngine:Thresholds`, §19 |
| 13 | Multi-timeframe weights | Weekly 40 / Daily 35 / H1 10 / M30 8 / M15 7 | `MultiTimeframe:Weights`, §19 |
| 14 | Risk per trade | 0.5% of capital | `RiskManager:RiskPerTradePercent`, §19 |
| 15 | Max concurrent open positions | 10 | `RiskManager:MaxConcurrentPositions`, §19 |
| 16 | Max sector concentration | 25% of deployed capital in one sector | `RiskManager:MaxSectorConcentrationPercent`, §19 |
| 17 | Correlation exposure limit | No more than 3 concurrent positions with pairwise return correlation > 0.7 | `RiskManager:MaxCorrelatedPositions`, §19 |
| 18 | Stop-loss floor | `max(structural stop, entry − 1.5×ATR14)` | §16.1 |
| 19 | Data quality gate | ≥30 non-zero-volume days in trailing 60; ≤10 consecutive no-trade days | `DataQualityGate`, §19 |
| 20 | Corporate-action reconciliation | Re-check trailing 90 days of Daily candles; overwrite if AdjClose diverges >0.1% | `Reconciliation`, §19 |
| 21 | Yahoo request throttling | 2 requests/second default, verify safe rate empirically before full-universe runs | `DataProviders:Yahoo`, §19 |
| 22 | Scanner Stage-2 shortlist size | Top 30 candidates by Stage-1 confidence | `Scanner:Stage2ShortlistSize`, §19 |
| 23 | Nifty Microcap 250 source | NSE Indices' official published constituent list (`nseindia.com` / `nsearchives.nseindia.com`), not a third-party aggregator | §6.5 |
| 24 | Universe rebalance cadence | Semi-annual, matching NSE Indices' published methodology — snapshot-based, not live-inferred | §6.5 |

---

## 3. Shared Infrastructure Components

Check your workspace for existing implementations first (§0). If none exist, implement these exactly as specified — every other layer of this system depends on them.

### 3.1 `CircularBuffer<T>`
Fixed-capacity ring buffer used for all rolling-window indicator state (moving averages, ATR windows, etc.).
```csharp
public sealed class CircularBuffer<T> : IEnumerable<T>
{
    public CircularBuffer(int capacity);
    public int Capacity { get; }
    public int Count { get; }
    public bool IsFull { get; }
    public void Add(T item);              // O(1); overwrites oldest once full
    public T this[int indexFromNewest] { get; }
    // IEnumerable yields items oldest-to-newest
}
```

### 3.2 `IBarProcessor`
Breaks circular dependencies between indicators that depend on each other's output for the same closed bar (e.g., ATR feeding SuperTrend, structure events feeding the decision engine).
```csharp
public interface IBarProcessor
{
    int Priority { get; }                 // lower runs first; ATR before anything consuming it
    Task OnBarClosedAsync(Candle bar, IProcessingContext ctx, CancellationToken ct);
}
```
A `BarProcessingPipeline` runs all registered `IBarProcessor` instances for a newly-closed bar in `Priority` order, writing each processor's output into `IProcessingContext` so later processors in the same pass can read it. This is also where the no-repaint rule (§21) is enforced architecturally: the pipeline only ever receives bars that are confirmed closed.

### 3.3 IST timezone resolution
Cross-platform utility resolving to the `Asia/Kolkata` IANA timezone id, with a fallback to Windows' `India Standard Time` id on platforms that need it (`TimeZoneInfo.FindSystemTimeZoneById` behaves differently across OSes). All candle timestamps are stored and compared in this timezone consistently — never in server-local time or unqualified UTC without conversion at the boundary.

### 3.4 Resilience policy (Polly)
One shared policy wrap applied to every outbound HTTP client (Yahoo, broker adapter):
- **Retry:** 3 attempts, exponential backoff (e.g., 2s, 4s, 8s) on transient failures (5xx, timeout, network error).
- **Circuit breaker:** open after 5 consecutive faults, break duration 30s.
- **Timeout:** per-request timeout, default 15s (configurable per provider, §19).

Register via typed `HttpClient` + `IHttpClientFactory` with the combined policy attached, not per-call try/catch scattered through provider code.

### 3.5 Clean Architecture / DI / Options conventions
- Layers: `Domain` (no dependencies) → `Application` (interfaces, orchestration, no I/O) → `Infrastructure.*` (I/O implementations) → `Cli` / `Web` (composition root).
- All tunable values bind from `appsettings.json` via `IOptions<T>` / `IOptionsMonitor<T>` — no hardcoded thresholds in business logic.
- Constructor injection throughout; no service locator pattern.
- `CancellationToken` threaded through every async method that does I/O or bounded computation.
- `ILogger<T>` injected wherever there's a decision point worth logging (provider fallback, gate failure, hard-gate trigger).

---

## 4. Solution Structure

```
NiftyMicrocapEngine/
  src/
    NiftyMicrocapEngine.Domain/
    NiftyMicrocapEngine.Application/
      Indicators/            # IIndicator + implementations
      Structure/              # market structure + SMC approximation engines
      Decision/                # decision engine, confidence model, risk manager
      Scanning/                # two-stage scanner
    NiftyMicrocapEngine.Infrastructure.YahooFinance/
    NiftyMicrocapEngine.Infrastructure.BrokerData/
    NiftyMicrocapEngine.Infrastructure.Persistence/    # SQLite repositories + migrations
    NiftyMicrocapEngine.Web/               # HTML dashboard + charting terminal
    NiftyMicrocapEngine.Cli/
  tests/
    NiftyMicrocapEngine.Domain.Tests/
    NiftyMicrocapEngine.Application.Tests/
    NiftyMicrocapEngine.Infrastructure.Tests/          # fixture-based, no live network calls
```

---

## 5. Domain Model

### 5.1 Candle
```csharp
public record Candle(
    int SymbolId, Timeframe Timeframe, DateTimeOffset Timestamp,
    decimal Open, decimal High, decimal Low, decimal Close, decimal AdjClose, long Volume);
```
Derived values (computed by a `CandleSeriesCalculator` that consumes an ordered sequence, since several need the prior candle): Typical Price, Median Price, True Range, ATR, Log Return, Gap, Body Size, Upper/Lower Wick, Body %, Range %.

### 5.2 Timeframe
```csharp
public enum Timeframe { Daily, Weekly, H1, M30, M15 }   // no Monthly, no 1m/5m — §2 item 1-3
```

### 5.3 Supporting records
```csharp
public record Symbol(int SymbolId, string NseSymbol, string CompanyName, string Sector, bool IsActive);
public record SymbolMapping(int SymbolId, DataProviderKind Provider, string ExternalId, DateOnly EffectiveFrom, DateOnly? EffectiveTo);
public enum DataProviderKind { Yahoo, Broker }
public record CorporateAction(int SymbolId, DateOnly ExDate, CorporateActionType Type, decimal Ratio);
public enum CorporateActionType { Split, Bonus, Dividend }
public record DataQualityFlag(int SymbolId, DateOnly AsOfDate, string FlagType, string? Detail);
public record MarketStructureEvent(int SymbolId, Timeframe Timeframe, DateTimeOffset Timestamp, StructureEventType EventType, string? Detail);
public enum StructureEventType { SwingHigh, SwingLow, HigherHigh, HigherLow, LowerHigh, LowerLow, BOS, CHoCH, OrderBlockBullish, OrderBlockBearish, FVGBullish, FVGBearish, LiquidityGrab, ExhaustionCandle }
public record IndicatorSnapshot(int SymbolId, Timeframe Timeframe, DateTimeOffset Timestamp, string IndicatorKey, decimal? Value, string? SignalState);
public record AnalysisResult(int SymbolId, DateOnly AsOfDate, string Decision, decimal Confidence, string LayerScoresJson, string ReasoningText, string? HardGateFailed);
public record TradeSignal(int AnalysisId, decimal Entry, decimal StopLoss, decimal Target1, decimal Target2, decimal Target3, decimal RiskPercent, decimal RiskRewardRatio, string InvalidationLevel);
```

---

## 6. Data Layer

### 6.1 Provider abstraction
```csharp
public interface IHistoricalDataProvider
{
    DataProviderKind Kind { get; }
    Task<IReadOnlyList<Candle>> GetCandlesAsync(SymbolMapping mapping, Timeframe timeframe, DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyList<CorporateAction>> GetCorporateActionsAsync(SymbolMapping mapping, CancellationToken ct);
}
public interface IHistoricalDataRouter
{
    Task<IReadOnlyList<Candle>> GetCandlesAsync(Symbol symbol, Timeframe timeframe, DateOnly from, DateOnly to, CancellationToken ct);
}
```

### 6.2 `YahooFinanceProvider`
Used for Daily/Weekly (full history, no meaningful retention limit). Verify the current unofficial endpoint shape empirically before finalizing the client — it's undocumented and can change. Known-current constraints to design around (verify at implementation time):

| Interval | Max lookback |
|---|---|
| 1m | ~7 days |
| 2m/5m/15m/30m/60m/90m/1h | ~60 days |
| 1d/5d/1wk/1mo/3mo | Full history |

This is *why* H1/M30/M15 route to the broker provider by default (§6.4) — Yahoo alone can't sustain a useful confirmation window.

### 6.3 `BrokerHistoricalDataProvider`
Wraps a broker's historical-candle API (Zerodha Kite Connect preferred; Upstox as alternative). Check your workspace for an existing broker adapter/credential-handling implementation first (§0) — if present, extend it with a historical-fetch method rather than building OAuth/token handling from scratch. If nothing exists, implement fresh: standard OAuth-style login flow producing a daily-refreshed access token, stored securely (not in source control or plain appsettings), used to authenticate historical-data requests.

Verified Kite Connect retention (confirm Upstox's equivalent empirically if that's the adapter in use):

| Interval | Retention |
|---|---|
| minute/2minute | 60 days |
| 3/4/5/10minute | 100 days |
| 15/30minute | 200 days |
| 60minute | 400 days |
| day/week | 2000 days |

Kite Connect's historical API is billed separately from order/trading access — confirm it's an active subscription before assuming this provider is usable; if not, it fails closed (empty result + log, not an exception) and the router falls back to Yahoo.

### 6.4 Routing policy
```json
"DataProviderRouting": {
  "Daily": "Yahoo", "Weekly": "Yahoo",
  "H1": "Broker", "M30": "Broker", "M15": "Broker",
  "FallbackToYahooOnBrokerFailure": true
}
```
On broker failure with fallback enabled: retry via Yahoo for whatever lookback it can serve, and write a `DataQualityFlag` (`FlagType = "ReducedConfirmationLookback"`) rather than silently treating shortened data as full-depth.

### 6.5 Universe management
- Source: NSE Indices' official Nifty Microcap 250 constituent list (`nseindia.com` / `nsearchives.nseindia.com`). Parse whatever format is currently published — verify the format at implementation time rather than assuming a fixed CSV schema.
- **Access note:** `nseindia.com` is known to block plain HTTP clients (User-Agent sniffing, session-cookie requirements, rate limiting). A bare GET will likely fail with a 403 even when the underlying data is available. Plan for a session-replay approach (visit the index/landing page first to acquire cookies and realistic headers, then request the data endpoint) as the default approach; fall back to a headless-browser fetch only if that's insufficient. See §24 Phase 0.
- Stored as versioned `UniverseSnapshots` (semi-annual rebalance per NSE Indices methodology) + `UniverseSnapshotMembers` — never a single live-mutable list, or backtests become survivorship-biased.
- `SymbolMapping` is per-provider (Yahoo ticker with `.NS` suffix; broker instrument token/key) with independent effective-date ranges — renames/delistings don't necessarily land on the same date across providers.

### 6.6 Corporate-action reconciliation
Scheduled job: re-fetch the trailing N days (default 90, §2 item 20) of Daily candles for every active symbol via Yahoo; overwrite stored AdjClose where it diverges beyond tolerance (default 0.1%). This is the only correct way to catch Yahoo's retroactive adjusted-close rewrites without re-downloading full history on every run. Log every overwrite (symbol, date, old/new value) as an audit trail.

### 6.7 Data quality gate
Before a symbol enters analysis, evaluate: minimum non-zero-volume trading days in a trailing window (default 30 of 60), maximum consecutive no-trade/circuit-locked days (default 10), and gaps in the expected trading calendar beyond NSE holidays (verify current holiday-list source at implementation time). Failing symbols are excluded and flagged with the specific reason, not silently dropped.

### 6.8 Circuit-band awareness
Track each symbol's current circuit-band state. A "Buy" recommendation on a stock locked at its upper circuit isn't actionable — the trade plan and confidence model must know this and downgrade/flag the signal rather than presenting an unreachable entry as tradable (feeds the Decision Engine's hard gates, §14).

---

## 7. Indicator Framework

```csharp
public interface IIndicator
{
    string Key { get; }
    int WarmupPeriod { get; }
    decimal? CurrentValue { get; }
    IReadOnlyList<decimal?> HistoricalValues { get; }
    string SignalState { get; }        // e.g. "Bullish", "Bearish", "Neutral"
    decimal Confidence { get; }        // 0-1, indicator's own certainty given warmup/data quality
    IndicatorHealth Health { get; }    // e.g. "OK", "InsufficientData", "Stale"
}
```

**Phase-1 core set** (ships first, from scratch, no external libraries):
- Trend: EMA, SMA, HMA, VWMA, SuperTrend, Donchian Channel
- Momentum: RSI, MACD, Stochastic, ADX/DMI
- Volume: OBV, VWAP, Rolling VWAP, Volume SMA, Volume Spike Detection
- Volatility: ATR, Bollinger Bands, Standard Deviation, Historical Volatility

**Phase-2 extended set** (pluggable additions, after the core decision engine is validated):
WMA, DEMA, TEMA, KAMA, Ichimoku, Regression Channel, Stochastic RSI, CCI, ROC, Williams %R, TRIX, CMF, MFI, Volume EMA, Keltner Channel, Range Compression/Expansion Detection.

Every indicator implements from scratch against `CircularBuffer<T>`-backed rolling state, registers as an `IBarProcessor` where its output feeds later processors (e.g., ATR → SuperTrend).

---

## 8. Market Structure Engine

Concrete, testable rule definitions (default parameters, all configurable — §19 `StructureThresholds`):

| Concept | Rule |
|---|---|
| Swing High/Low | 5-bar fractal: a bar is a swing high if its High is the max of itself and 2 bars each side (swing low analogous with Low/min) |
| Higher High / Higher Low | A new confirmed swing high/low that's above the prior confirmed swing high/low (lower high/low analogous) |
| Break of Structure (BOS) | A closed candle's Close beyond the most recent unbroken swing point in the direction of the prevailing trend (continuation) |
| Change of Character (CHoCH) | The first BOS in the *opposite* direction of the prevailing structure — i.e., breaks the most recent swing point against the trend after a run of same-direction BOS events |
| Impulse leg | A price leg whose range ≥ 1.5× ATR(14), or that produces a BOS within 3 candles of the leg's start |
| Correction / Pullback | A counter-trend leg following an impulse leg that does not itself produce a CHoCH |
| Trend leg | Any leg between two confirmed swing points in the direction of the prevailing structure |
| Range / Consolidation | Price contained between the most recent unbroken swing high and swing low for ≥ N candles (default N=10) with no BOS |
| Accumulation / Distribution | A Range (above) occurring after a prior down-leg (accumulation) or up-leg (distribution), used as a Context Engine input (§13), not a structure event per se |

Every event is marked on the closed candle that confirms it — never on a forming candle (§21).

---

## 9. Smart-Money Approximation Layer

OHLCV-only formalizations of SMC/ICT concepts (default parameters, configurable — §19 `StructureThresholds`). These are standard, widely-used definitions in retail SMC/ICT literature — reasonable defaults for a system with no proprietary reference to draw from; revisit if you have a specific variant you prefer.

| Concept | Rule |
|---|---|
| Order Block (bullish) | The last down-close candle immediately preceding an impulse leg (§8) that breaks structure upward |
| Order Block (bearish) | The last up-close candle immediately preceding an impulse leg that breaks structure downward |
| Fair Value Gap (bullish) | Three consecutive candles where candle₁.High < candle₃.Low; gap zone = [candle₁.High, candle₃.Low] |
| Fair Value Gap (bearish) | candle₁.Low > candle₃.High; gap zone = [candle₃.High, candle₁.Low] |
| Supply / Demand zone | The origin candle range of an impulse leg (same impulse definition as Order Block) |
| Liquidity grab | A candle's wick pierces beyond a marked, unmitigated prior swing point, but the candle's Close remains back inside the prior range (the break is not confirmed on close) |
| Bull trap / Bear trap | A confirmed BOS (close beyond swing point) that reverses — price closes back inside the broken range within K candles (default K=3) |
| False breakout / Failed breakdown | Price exceeds a Range's boundary (§8) intra-candle but closes back inside the Range on the same or next candle |
| Volume absorption | A candle with Volume ≥ 2× the 20-period Volume SMA but Body% < 30% of Range, occurring at a marked supply/demand zone or swing point |
| Exhaustion candle | Range ≥ 2× ATR(14) with Close in the outer 20% of the range *against* the prevailing trend direction |
| Gap: breakaway | A price gap at the start of an impulse leg that breaks out of a prior Range |
| Gap: continuation | A price gap mid-trend in the direction of the prevailing trend, not at a Range boundary |
| Gap: exhaustion | A price gap late in an established trend (after ≥3 same-direction trend legs) followed by a reversal (CHoCH) within K candles (default K=5) |

---

## 10. Candle Psychology

| Pattern | Rule (defaults, configurable) |
|---|---|
| Doji | Body% < 10% of Range |
| Marubozu | Body% > 90% of Range |
| Pin bar | One wick > 66% of Range, Body% < 33%, opposite wick small (<10% of Range) |
| Engulfing (bullish/bearish) | Candle₂ body fully contains Candle₁ body and is the opposite color |
| Harami | Candle₂ body fully inside Candle₁ body, opposite color |
| Inside bar | Candle₂'s High/Low range fully inside Candle₁'s range |
| Outside bar | Candle₂'s High/Low range fully outside (engulfs) Candle₁'s range |
| Morning Star | Long bearish candle → small body/doji gapping down → long bullish candle closing beyond the midpoint of candle 1 |
| Evening Star | Mirror of Morning Star, bullish → small body/doji gapping up → long bearish candle |
| Three White Soldiers | Three consecutive bullish candles, each closing higher, each Body% > 60%, no long upper wicks |
| Three Black Crows | Mirror, bearish |

Also computed per candle (feeds Decision Engine §14, layer "Candle Psychology"): Body%, Upper/Lower Wick%, Range expansion vs. ATR, Relative Volume vs. 20-period average, Close location within the candle's range.

---

## 11. Trend / Momentum / Volume / Volatility Engines

- **Trend Engine:** combines EMA/SMA slope, SuperTrend direction, and structure state (§8) into Primary/Intermediate/Short trend classifications: Strong Bull, Bull, Weak Bull, Neutral, Weak Bear, Bear, Strong Bear. Also computes Trend Strength, Acceleration (rate of slope change), Quality (consistency of higher-highs/higher-lows), Exhaustion (extended distance from mean without pullback), and a 0-1 Confidence.
- **Momentum Engine:** classifies RSI/MACD/Stochastic state as Increasing, Weakening, Accelerating, Diverging (price vs. indicator), Exhausted, Reversing. Outputs a 0-100 score.
- **Volume Engine:** classifies relative volume against 20-period average into Institutional buying/selling (large relative volume with strong directional close), Retail move (moderate volume, small directional close), Low/High participation, Volume Climax (extreme relative volume with reversal candle), Dry Volume, Distribution/Accumulation Volume (per §8's Range classification).
- **Volatility Engine:** ATR regime (percentile of trailing 100-period ATR), Bollinger Band width percentile for Compression/Expansion, and a Breakout Probability heuristic (compression duration × proximity to Range boundary).

Each engine's output is a scoring input to the Decision Engine (§14), not a standalone recommendation.

---

## 12. Multi-Timeframe Engine

Stack: Weekly → Daily (primary) → H1 → M30 → M15 (confirmation-only). Default weights (§2 item 13): Weekly 40 / Daily 35 / H1 10 / M30 8 / M15 7 — configurable via `MultiTimeframe:Weights` (§19). **Must renormalize** when a configured timeframe is unavailable for a given symbol/run (e.g., broker fetch failure) — remaining weights scale to sum to 100% rather than silently understating confidence.

---

## 13. Market Regime Filter & Relative Strength

- **Regime Filter:** run the full Trend/Structure analysis (§8, §11) on Nifty 50 and Nifty Midcap indices *before* scoring individual microcaps. During confirmed broad-market weakness (Nifty 50 Primary Trend = Bear or Strong Bear), tighten the confidence threshold required for Buy/Strong Buy and suppress new long signals unless the individual setup scores above a configurable override threshold (default: requires ≥90 confidence to override, vs. the normal ≥65 for Buy). This is a Decision Engine hard-gate input (§14), not just informational.
- **Relative Strength:** for each microcap, compute return-ratio vs. Nifty Microcap 250 index and vs. Nifty 50 over configurable lookback windows (default: 20 and 60 trading days). Feeds the Decision Engine's "Relative Strength & Regime alignment" scoring layer (§14).

---

## 14. Decision Engine

Two stages — hard gates, then weighted scoring. Gates force `No Trade` regardless of score; scoring never overrides a failed gate.

**Hard gates (any failure → No Trade, reason stated in output):**
- Data quality gate failed (§6.7)
- Symbol circuit-locked against the intended direction (§6.8)
- Market regime filter in "suppress longs" state and setup score below the override threshold (§13)
- Confirmed structure break against the proposed trade direction on the primary (Daily) timeframe within the lookback window

**Weighted scoring** (default point budget, §2 item 11 — must sum to 100):

| Layer | Points |
|---|---|
| Market Structure | 25 |
| Trend | 20 |
| Momentum | 15 |
| Volume | 15 |
| Volatility / Opportunity | 10 |
| Candle Psychology | 5 |
| Support/Resistance proximity | 5 |
| Relative Strength & Regime alignment | 5 |

Each layer contributes a signed sub-score within its own budget (can go negative within the layer, e.g. late-trend exhaustion subtracts from the Trend layer's own allocation) — additive and auditable, never a separate bolt-on penalty. Sum → 0-100 confidence.

**Output mapping** (default thresholds, §2 item 12):

| Confidence | Decision |
|---|---|
| ≥ 80 | Strong Buy |
| 65–79 | Buy |
| 50–64 | Watch |
| 35–49 | Hold |
| 20–34 | Sell |
| < 20 | Strong Sell / Exit |
| Hard gate failed | No Trade (independent of score) |

---

## 15. Explainability

Every `AnalysisResult` includes: the per-layer point contribution table from §14, and a human-readable reasoning chain built from the structure/trend/momentum/volume/psychology facts that drove each layer's sub-score (e.g., "Primary trend remains bullish. Price is above the 50 EMA and SuperTrend. Higher highs and higher lows remain intact. Volume expanded 2.3× on breakout. Current pullback has low volume. Momentum remains positive. Confidence 84%."). This is a hard requirement — it's the only way to audit whether the §2 default weights are behaving sensibly during Phase 6 validation.

---

## 16. Risk Manager & Trade Engine

### 16.1 Per-trade
- Stop = `max(structural stop below last swing low / demand zone, entry − 1.5×ATR14)` — the ATR floor prevents unrealistically tight stops where the structural stop sits implausibly close to entry in illiquid microcaps.
- Targets: 1R / 2R / 3R, or the next resistance/supply zone (§9), whichever is nearer.
- Risk % = (Entry − Stop) / Entry. Risk:Reward computed from Entry/Stop/Target.
- Trade Duration: estimated from the primary timeframe's average impulse-leg length over the trailing 6 months (heuristic, refine during Phase 6 validation). If insufficient history exists to compute this (e.g. a recent listing, or fewer than 3 qualifying impulse legs in the trailing window), do not substitute a global fallback number — emit `null`/`N/A` and set a `DataQualityFlag` (`FlagType = "InsufficientHistoryForDurationEstimate"`) rather than presenting a fabricated estimate as real.
- Invalidation Level: the structure event that, if it occurs, invalidates the trade thesis (e.g., "CHoCH below ₹X" for a long).

### 16.2 Portfolio-level (defaults, §2 items 14-17)
- Risk per trade: 0.5% of capital.
- Max concurrent open positions: 10.
- Max sector concentration: 25% of deployed capital.
- Max correlated exposure: no more than 3 concurrent positions with pairwise 60-day return correlation > 0.7 (simple pairwise check — full portfolio optimization is out of scope).

A technically excellent setup that would breach a portfolio limit is flagged `Watch` with the specific limit named, not silently promoted to `Buy`.

---

## 17. Scanner — Two-Stage Funnel

**Stage 1 (coarse, full universe, Daily/Weekly only):** run the full pipeline (§8–§15) for all 250 symbols using cached + incrementally-updated Daily/Weekly data. Cheap — mostly local computation.

**Stage 2 (fine, shortlist only, adds H1/M30/M15 confirmation):** top-N candidates from Stage 1 (default N=30, §2 item 22) get intraday data fetched (§6.4 routing) and the multi-timeframe engine's intraday weighting applied (§12).

Rank Stage-2 output by confidence, momentum, trend, risk-reward, expected return, relative strength.

**Performance targets, reported separately:** Stage 1 (all 250, mostly cached) — well under 5 minutes, local computation. Stage 2 (30 symbols, live intraday fetch) — target under 5 minutes for that stage specifically, subject to empirically-verified safe request rates.

---

## 18. Storage — Full SQLite Schema

```sql
CREATE TABLE Symbols (
    SymbolId INTEGER PRIMARY KEY,
    NseSymbol TEXT NOT NULL,
    CompanyName TEXT,
    Sector TEXT,
    IsActive INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE SymbolMapping (
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    Provider TEXT NOT NULL,
    ExternalId TEXT NOT NULL,
    EffectiveFrom TEXT NOT NULL,
    EffectiveTo TEXT NULL
);

CREATE TABLE UniverseSnapshots (
    SnapshotId INTEGER PRIMARY KEY,
    EffectiveDate TEXT NOT NULL,
    IndexName TEXT NOT NULL DEFAULT 'NIFTY_MICROCAP_250',
    SourceDocument TEXT
);

CREATE TABLE UniverseSnapshotMembers (
    SnapshotId INTEGER NOT NULL REFERENCES UniverseSnapshots(SnapshotId),
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    PRIMARY KEY (SnapshotId, SymbolId)
);

CREATE TABLE Candles (
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    Timeframe TEXT NOT NULL,
    Timestamp TEXT NOT NULL,
    Open REAL NOT NULL, High REAL NOT NULL, Low REAL NOT NULL,
    Close REAL NOT NULL, AdjClose REAL NOT NULL, Volume INTEGER NOT NULL,
    PRIMARY KEY (SymbolId, Timeframe, Timestamp)
);

CREATE TABLE DataQualityFlags (
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    AsOfDate TEXT NOT NULL,
    FlagType TEXT NOT NULL,
    Detail TEXT,
    PRIMARY KEY (SymbolId, AsOfDate, FlagType)
);

CREATE TABLE IndicatorValues (
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    Timeframe TEXT NOT NULL,
    Timestamp TEXT NOT NULL,
    IndicatorKey TEXT NOT NULL,
    Value REAL,
    SignalState TEXT,
    PRIMARY KEY (SymbolId, Timeframe, Timestamp, IndicatorKey)
);

CREATE TABLE MarketStructureEvents (
    EventId INTEGER PRIMARY KEY,
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    Timeframe TEXT NOT NULL,
    Timestamp TEXT NOT NULL,
    EventType TEXT NOT NULL,
    Detail TEXT
);

CREATE TABLE Analysis (
    AnalysisId INTEGER PRIMARY KEY,
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    AsOfDate TEXT NOT NULL,
    Decision TEXT NOT NULL,
    Confidence REAL NOT NULL,
    LayerScoresJson TEXT NOT NULL,
    ReasoningText TEXT NOT NULL,
    HardGateFailed TEXT NULL
);

CREATE TABLE TradeSignals (
    SignalId INTEGER PRIMARY KEY,
    AnalysisId INTEGER NOT NULL REFERENCES Analysis(AnalysisId),
    Entry REAL, StopLoss REAL, Target1 REAL, Target2 REAL, Target3 REAL,
    RiskPercent REAL, RiskRewardRatio REAL, InvalidationLevel TEXT
);

CREATE TABLE ScanHistory (
    ScanId INTEGER PRIMARY KEY,
    RunAt TEXT NOT NULL,
    Stage1Count INTEGER, Stage2Count INTEGER,
    Stage1DurationMs INTEGER, Stage2DurationMs INTEGER
);
```
`IndicatorValues` is a key-value table deliberately — keeps the Phase-2 extended indicator set (§7) purely additive with no schema migration required per new indicator.

---

## 19. Configuration — `appsettings.json`

```json
{
  "DataProviders": {
    "Yahoo": { "RequestsPerSecond": 2, "TimeoutSeconds": 15, "RetryCount": 3 },
    "Broker": { "PreferredBroker": "Zerodha", "TimeoutSeconds": 15, "RetryCount": 3 }
  },
  "DataProviderRouting": {
    "Daily": "Yahoo", "Weekly": "Yahoo",
    "H1": "Broker", "M30": "Broker", "M15": "Broker",
    "FallbackToYahooOnBrokerFailure": true
  },
  "Reconciliation": { "LookbackDays": 90, "AdjCloseToleranceFraction": 0.001 },
  "DataQualityGate": { "TrailingWindowDays": 60, "MinimumNonZeroVolumeDays": 30, "MaxConsecutiveNoTradeDays": 10 },
  "StructureThresholds": {
    "SwingFractalBars": 2,
    "ImpulseAtrMultiple": 1.5,
    "ImpulseBosLookaheadCandles": 3,
    "RangeMinCandles": 10,
    "TrapReversalLookaheadCandles": 3,
    "ExhaustionGapReversalLookaheadCandles": 5
  },
  "MultiTimeframe": { "Weights": { "Weekly": 40, "Daily": 35, "H1": 10, "M30": 8, "M15": 7 } },
  "DecisionEngine": {
    "LayerWeights": { "Structure": 25, "Trend": 20, "Momentum": 15, "Volume": 15, "Volatility": 10, "Psychology": 5, "SupportResistance": 5, "RelativeStrengthRegime": 5 },
    "Thresholds": { "StrongBuy": 80, "Buy": 65, "Watch": 50, "Hold": 35, "Sell": 20 },
    "RegimeOverrideConfidence": 90
  },
  "RelativeStrength": { "LookbackDaysShort": 20, "LookbackDaysLong": 60 },
  "RiskManager": {
    "RiskPerTradePercent": 0.5,
    "MaxConcurrentPositions": 10,
    "MaxSectorConcentrationPercent": 25,
    "MaxCorrelatedPositions": 3,
    "CorrelationThreshold": 0.7,
    "StopAtrMultiple": 1.5
  },
  "Scanner": { "Stage2ShortlistSize": 30 },
  "Storage": { "SqliteConnectionString": "Data Source=niftymicrocapengine.db" }
}
```

---

## 20. UI — HTML Dashboard & Charting Terminal

Check your workspace for an existing dashboard shell first (§0) — if one exists with a compatible visual/component pattern, extend it rather than starting fresh. Otherwise, build per this spec:

### 20.1 Visual design
Dark, data-dense "trading terminal" theme: near-black background (`#0d1117`-range), high-contrast monospace/tabular type for numbers, a restrained accent palette — green/red for directional confidence bands, amber for Watch/Hold, blue-grey for neutral chrome. Avoid decorative styling; every pixel should communicate data density and precision.

### 20.2 Views
- **Scan Results:** ranked table (confidence, decision, symbol, sector, R:R, relative strength), filterable by decision and sector, sortable by any scored column.
- **Symbol Drill-Down:** full explanation reasoning chain (§15) with the per-layer point table rendered as a visible breakdown (not just a number), trade plan (entry/stop/targets/R:R), data-quality flags if any, circuit-band state if relevant.
- **Charting Terminal:** candlestick chart per symbol/timeframe with structure/SMC annotations overlaid (swing points, BOS/CHoCH markers, order blocks, FVG zones) — build this after the underlying signals are validated (Phase 7), not before, to avoid redoing overlay work when Decision Engine weights change during Phase 6.

### 20.3 Charting library
TradingView's Lightweight Charts (open-source, purpose-built for OHLC/candlestick rendering) unless a different library is already established elsewhere in the workspace — prefer consistency with existing patterns over this specific recommendation if one exists.

---

## 21. No-Repaint Policy

All indicator values, structure events, and decisions are computed only from fully closed candles. No layer may use the currently-forming candle's values in a way that would change once the candle closes. Enforced architecturally: `IBarProcessor` (§3.2) only ever receives confirmed-closed bars. Verified by an explicit regression test category (§22).

---

## 22. Testing Strategy

- Golden-file tests for every indicator, verified against an independent reference calculation.
- Property-based tests for structure invariants (e.g., a marked Higher High must be a local maximum over its lookback/lookforward window).
- No-repaint regression suite: for a sample of symbols, assert no previously-computed value/event/decision for a closed candle ever changes on a subsequent run.
- Decision Engine tests: hard gates force `No Trade` independent of score; weighted scoring sums correctly; threshold mapping is correct at every boundary.
- Regime-filter override tests (§13/§14 interaction, tested explicitly and separately from generic hard-gate tests — this is the one gate most likely to be implemented backwards): when regime is in "suppress longs" state, a setup scoring below the override threshold (default 90) must resolve to `No Trade`/gate-failed, not merely a downweighted score; a setup scoring at or above the override threshold must be allowed through; a setup scoring just below it (e.g. 89) must still be suppressed. Assert the gate short-circuits scoring rather than being applied as a post-hoc penalty.
- Walk-forward backtest harness tests (mechanical correctness of rolling-window logic, not strategy performance).
- Infrastructure tests against recorded fixtures only — never live Yahoo/broker endpoints in CI.
- Symbol mapping tests: effective-date lookups, including a symbol with a valid mapping for one provider but not the other.
- CLI/command smoke tests end-to-end against a temp SQLite DB with fixture data.

---

## 23. Performance Targets

Stage 1 (§17): all 250 symbols, Daily/Weekly, well under 5 minutes (local computation, cached data). Stage 2: 30-symbol shortlist with live intraday fetch, target under 5 minutes for that stage specifically, subject to empirically-verified safe provider request rates. Report both numbers separately in the benchmark deliverable — never collapse them into a single claim that hides where time is actually spent.

---

## 24. Phased Delivery Roadmap

**Phase 0 — Data Access Smoke Test (do this before any other code):** Yahoo's Daily/Weekly endpoint (§6.2) is unofficial and undocumented — it can and does change shape without notice. Before writing the provider implementation, write a small standalone script that hits the live endpoint for 2-3 known symbols (e.g. `RELIANCE.NS`) across Daily and Weekly, and confirms the response shape matches what §6.2 assumes. Also do a standalone connectivity check against `nseindia.com`/`nsearchives.nseindia.com` (§6.5) — this host is known to block plain HTTP clients via User-Agent sniffing, session-cookie requirements, and rate limiting, so a bare GET is likely to fail even when the endpoint itself is fine. If it fails, confirm before proceeding whether a session-replay approach (fetch the index page first to acquire cookies, then request the data endpoint with browser-like headers) resolves it, or whether a headless-browser fetch is required. Do not proceed to Phase 1 until both checks pass against live endpoints — this is the cheapest possible point to catch an endpoint change or access block, and the most expensive point to discover one is three phases in.

**Phase 1 — Foundation:** §3 shared infrastructure, §4 solution skeleton, §6 data layer (both providers, routing, universe management, reconciliation, quality gate), §18 schema (Symbols/SymbolMapping/UniverseSnapshots/UniverseSnapshotMembers/Candles/DataQualityFlags only), CLI backfill/reconcile/scan-quality commands. No indicators yet.

**Phase 2 — Core Indicators & Structure:** §7 Phase-1 indicator set, §10 candle psychology, §8 market structure engine.

**Phase 3 — Smart-Money Layer & Multi-Timeframe:** §9 SMC approximation, §12 multi-timeframe engine, §13 regime filter & relative strength.

**Phase 4 — Decision Engine:** §14 weighted scoring + hard gates, §15 explainability, §16 risk manager & trade engine.

**Phase 5 — Scanner & Core UI:** §17 two-stage funnel, §20.1–20.2 scan-results dashboard and drill-down (not the charting terminal yet).

**Phase 6 — Validation:** walk-forward backtester, weight/threshold tuning against it (revisit every default in §2), benchmark report with Stage-1/Stage-2 timings reported separately.

**Phase 7 — Production Hardening:** §6.8 circuit-band awareness end-to-end, §6.6 reconciliation job running on schedule, §21 no-repaint regression suite, load test against the full 250-symbol universe, §20.2 charting terminal with annotation overlays, §7 Phase-2 extended indicators, full test suite, final benchmark.

---

## 25. Deliverables Checklist

- [ ] Complete source, Clean Architecture solution structure (§4)
- [ ] SQLite schema as migration scripts (§18)
- [ ] `appsettings.json` with all sections populated (§19)
- [ ] README covering setup, backfill process, how to run a scan, and broker-credential setup
- [ ] Architecture, dependency, class, and sequence diagrams
- [ ] Golden-file indicator test suite + no-repaint regression suite
- [ ] Walk-forward backtest harness + sample report
- [ ] Benchmark report with Stage-1/Stage-2 timings reported separately against the real 250-symbol universe
- [ ] Sample scan output showing the full explainability chain for at least one Strong Buy, one Hold, and one No Trade (hard-gate) case
- [ ] HTML dashboard (scan results + drill-down + charting terminal) running against real data
