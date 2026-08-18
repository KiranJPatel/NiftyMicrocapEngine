# Architecture, Dependency, Class, and Sequence Diagrams

Companion to README.md — the diagrams deliverable from the checklist. These
are Mermaid diagrams; GitHub, most Markdown viewers, and Claude.ai render
them inline. Kept close to what the code actually does as of this pass —
regenerate/update these if the code moves and they don't, rather than
trusting them as documentation-of-record.

## 1. Project dependency graph

Accurate to the actual `<ProjectReference>` entries in each `.csproj` at the
time of writing — not idealized, the real graph. Domain has no dependencies
(by design — see README's Architecture notes); Application depends only on
Domain; every Infrastructure project and the two hosts (Cli, Web) depend on
Application and Domain but never on each other.

```mermaid
graph TD
    Domain[NiftyMicrocapEngine.Domain]
    App[NiftyMicrocapEngine.Application]
    Yahoo[Infrastructure.YahooFinance]
    Broker[Infrastructure.BrokerData]
    Persistence[Infrastructure.Persistence]
    Cli[NiftyMicrocapEngine.Cli]
    Web[NiftyMicrocapEngine.Web]

    App --> Domain
    Yahoo --> Domain
    Yahoo --> App
    Broker --> Domain
    Broker --> App
    Persistence --> Domain
    Persistence --> App
    Cli --> Domain
    Cli --> App
    Cli --> Yahoo
    Cli --> Broker
    Cli --> Persistence
    Web --> Domain
    Web --> App
    Web --> Yahoo
    Web --> Broker
    Web --> Persistence
```

## 2. High-level architecture (layers, not projects)

```mermaid
graph TB
    subgraph Presentation
        WebUI[Dashboard HTML/JS<br/>Scan Results, Drill-Down, Charting Terminal]
        CliCmd[CLI commands<br/>scan / reconcile / benchmark / backtest]
    end

    subgraph "Application (Domain logic, provider-agnostic)"
        Scanner[UniverseScanner<br/>Stage 1 + Stage 2]
        Backtester[WalkForwardBacktester]
        Structure[Structure/SMC Pipeline<br/>Indicators, Swings, BOS/CHoCH, Zones, Events]
        MTF[MultiTimeframeEngine]
        Regime[RegimeFilter + RelativeStrengthCalculator]
        Decision[DecisionEngine<br/>Hard Gates -> Weighted Layers]
        Risk[TradePlanBuilder + PortfolioRiskManager]
        DQ[DataQualityGate + CircuitBandTracker]
    end

    subgraph "Infrastructure (I/O, providers)"
        Router[FallbackMarketDataRouter<br/>Yahoo -> NSE -> Zerodha]
        Cache[CachingMarketDataService]
        Reconcile[CorporateActionReconciliationJob]
        Sqlite[(SQLite<br/>via Dapper repositories)]
    end

    WebUI --> Scanner
    WebUI --> Backtester
    CliCmd --> Scanner
    CliCmd --> Backtester
    CliCmd --> Reconcile

    Scanner --> DQ
    Scanner --> Structure
    Scanner --> MTF
    Scanner --> Regime
    Scanner --> Decision
    Scanner --> Risk
    Backtester --> DQ
    Backtester --> Structure
    Backtester --> MTF
    Backtester --> Regime
    Backtester --> Decision
    Backtester --> Risk

    Structure --> Cache
    Scanner --> Cache
    Backtester --> Cache
    Reconcile --> Router
    Cache --> Router
    Scanner --> Sqlite
    Reconcile --> Sqlite
```

## 3. Decision pipeline class relationships

What actually feeds the Decision Engine for one symbol/timeframe/as-of-date
— this is the shape both `UniverseScanner.Stage1.cs` and
`WalkForwardBacktester.WalkOneSymbolAsync` build, independently (see the
backtester's own doc comment on why it doesn't share UniverseScanner's
private methods).

```mermaid
classDiagram
    class StructureAnalysisPipelineFactory {
        +Create(symbolId, timeframe, thresholds) Handles
    }
    class Handles {
        +BarProcessingPipeline Pipeline
        +SwingPointDetector SwingPoints
        +StructureBreakDetector StructureBreaks
        +SmcZoneDetector SmcZones
        +SmcEventDetector SmcEvents
        +AtrIndicator Atr
        +IIndicator[] AllIndicators
        +SnapshotIndicatorValues() Dictionary
    }
    class DecisionEngineInput {
        +int SymbolId
        +DateOnly AsOfDate
        +TrendDirection ProposedDirection
        +StructureSnapshot Structure
        +Dictionary IndicatorValues
        +MtfAlignmentResult Mtf
        +RegimeFilterResult Regime
        +RelativeStrengthResult RelativeStrength
        +bool IsCircuitLockedAgainstDirection
        +bool HasStructureBreakAgainstDirection
    }
    class DecisionEngine {
        +Evaluate(DecisionEngineInput) DecisionEngineResult
    }
    class DecisionEngineResult {
        +DecisionOutcome Outcome
        +decimal ConfidenceScore
        +LayerScore[] LayerScores
        +HardGateKind? HardGateFailed
    }
    class TradePlanBuilder {
        +Build(TradePlanRequest) TradePlan
    }
    class TradePlan {
        +decimal Entry
        +decimal StopLoss
        +decimal Target1
        +decimal Target2
        +decimal Target3
        +TimeSpan? EstimatedDuration
    }

    StructureAnalysisPipelineFactory --> Handles : creates
    Handles --> DecisionEngineInput : feeds IndicatorValues + StructureSnapshot
    DecisionEngineInput --> DecisionEngine : input to
    DecisionEngine --> DecisionEngineResult : produces
    DecisionEngineResult --> TradePlanBuilder : Buy/StrongBuy triggers
    TradePlanBuilder --> TradePlan : produces
```

## 4. Live scan sequence (`UniverseScanner.RunAsync`)

```mermaid
sequenceDiagram
    participant Caller as CLI/Dashboard
    participant Scanner as UniverseScanner
    participant Cache as CachingMarketDataService
    participant Pipeline as StructureAnalysisPipeline
    participant Decision as DecisionEngine
    participant Risk as TradePlanBuilder
    participant DB as SQLite

    Caller->>Scanner: RunAsync(asOfDate)
    Scanner->>DB: GetLatestSnapshotAsync()
    Scanner->>Scanner: BroadMarketContextProvider.GetContextAsync(asOfDate) [once for the whole run]

    loop Stage 1: every universe symbol (bounded parallelism)
        Scanner->>Cache: GetCandlesAsync(Daily/Weekly)
        Scanner->>Pipeline: run full history through pipeline
        Pipeline-->>Scanner: structure snapshot + indicator values
        Scanner->>Decision: Evaluate(...)
        Decision-->>Scanner: outcome + confidence
    end

    Scanner->>Scanner: rank Stage 1 results, take top N (Stage2ShortlistSize)

    loop Stage 2: shortlisted symbols only
        Scanner->>Cache: GetCandlesAsync(intraday timeframes)
        Scanner->>Decision: re-evaluate with MTF alignment
        Decision-->>Scanner: refined outcome
        alt Buy or StrongBuy
            Scanner->>Risk: Build(TradePlanRequest)
            Risk-->>Scanner: TradePlan
        end
    end

    Scanner->>DB: persist IndicatorValues, MarketStructureEvents, ScanHistory
    Scanner-->>Caller: ScanRunResult (Stage1Results, Stage2Results)
```

## 5. Walk-forward backtest sequence

```mermaid
sequenceDiagram
    participant Caller as CLI (backtest command)
    participant BT as WalkForwardBacktester
    participant Cache as CachingMarketDataService
    participant Regime as BroadMarketContextProvider
    participant Decision as DecisionEngine
    participant Sim as BacktestOutcomeSimulator

    Caller->>BT: RunAsync(BacktestRequest)
    BT->>Cache: GetCandlesAsync(symbol, 2yr warmup .. EndDate) [once per symbol, not per date]

    loop each symbol
        loop each simulated as-of date (cadence-spaced, no lookahead)
            BT->>BT: slice candles <= as-of date only
            BT->>Regime: GetContextAsync(as-of date) [fresh per date, unlike live scan]
            BT->>Decision: Evaluate(...)
            alt Buy or StrongBuy
                BT->>Sim: Simulate(plan, forward candles strictly AFTER as-of date)
                Sim-->>BT: BacktestTradeOutcome (win/loss/timeout, R-multiple)
            end
        end
    end

    BT->>BT: aggregate into BacktestBucketStats (StrongBuy vs Buy)
    BT-->>Caller: BacktestReport
    Caller->>Caller: BacktestReportFormatter -> Markdown + CSV files
```

## Honesty note

These diagrams were authored by reading the actual source (constructor
signatures, method call order) rather than from the original spec text, so
they should track what's really implemented — but they haven't been
validated against a running system in this pass, for the same reason
nothing else here has (no .NET SDK or NuGet access in this sandbox). Treat
them as a reviewed-by-inspection map, and correct them the first time they
visibly diverge from the code.
