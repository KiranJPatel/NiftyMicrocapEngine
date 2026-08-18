namespace NiftyMicrocapEngine.Domain;

public sealed record Symbol(int SymbolId, string NseSymbol, string CompanyName, string Sector, bool IsActive);

public enum DataProviderKind { Yahoo, Broker }

public sealed record SymbolMapping(
    int SymbolId,
    DataProviderKind Provider,
    string ExternalId,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo);

public enum CorporateActionType { Split, Bonus, Dividend }

public sealed record CorporateAction(int SymbolId, DateOnly ExDate, CorporateActionType Type, decimal Ratio);

public sealed record DataQualityFlag(int SymbolId, DateOnly AsOfDate, string FlagType, string? Detail);

public enum StructureEventType
{
    SwingHigh,
    SwingLow,
    HigherHigh,
    HigherLow,
    LowerHigh,
    LowerLow,
    BOS,
    CHoCH,
    OrderBlockBullish,
    OrderBlockBearish,
    FVGBullish,
    FVGBearish,
    LiquidityGrab,
    ExhaustionCandle,

    // Added to close the schema gap the original build pass flagged in
    // UniverseScanner.Persistence.cs: the structure engine's SmcEventDetector
    // (§9) detects these but this enum had no corresponding member, so they
    // were logged at Debug and silently never persisted to MarketStructureEvents.
    // EventType is stored as SQLite TEXT (see §18's schema — no fixed-width
    // column, no CHECK constraint enumerating values), so adding members here
    // needs no migration — existing rows are unaffected, and MapSmcEventKind
    // (UniverseScanner.Persistence.cs) is the only other place that needed
    // updating to complete the mapping.
    BullTrap,
    BearTrap,
    FalseBreakout,
    FailedBreakdown,
    VolumeAbsorption,
    GapBreakaway,
    GapContinuation,
    GapExhaustion
}

public sealed record MarketStructureEvent(
    int SymbolId,
    Timeframe Timeframe,
    DateTimeOffset Timestamp,
    StructureEventType EventType,
    string? Detail);

public sealed record IndicatorSnapshot(
    int SymbolId,
    Timeframe Timeframe,
    DateTimeOffset Timestamp,
    string IndicatorKey,
    decimal? Value,
    string? SignalState);

public sealed record AnalysisResult(
    int SymbolId,
    DateOnly AsOfDate,
    string Decision,
    decimal Confidence,
    string LayerScoresJson,
    string ReasoningText,
    string? HardGateFailed);

public sealed record TradeSignal(
    int AnalysisId,
    decimal Entry,
    decimal StopLoss,
    decimal Target1,
    decimal Target2,
    decimal Target3,
    decimal RiskPercent,
    decimal RiskRewardRatio,
    string InvalidationLevel);
