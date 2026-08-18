using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Persistence;

public interface ISymbolRepository
{
    Task<Symbol?> GetBySymbolIdAsync(int symbolId, CancellationToken ct = default);
    Task<Symbol?> GetByNseSymbolAsync(string nseSymbol, CancellationToken ct = default);
    Task<IReadOnlyList<Symbol>> GetAllActiveAsync(CancellationToken ct = default);
    Task<int> UpsertAsync(Symbol symbol, CancellationToken ct = default);
    Task SaveMappingAsync(SymbolMapping mapping, CancellationToken ct = default);
    Task<SymbolMapping?> GetActiveMappingAsync(int symbolId, DataProviderKind provider, DateOnly asOf, CancellationToken ct = default);
}

public interface IUniverseRepository
{
    Task<UniverseSnapshot?> GetLatestSnapshotAsync(CancellationToken ct = default);
    Task<int> SaveSnapshotAsync(UniverseSnapshot snapshot, IReadOnlyList<int> memberSymbolIds, CancellationToken ct = default);
    Task<IReadOnlyList<int>> GetMemberSymbolIdsAsync(int snapshotId, CancellationToken ct = default);
}

public interface ICandleRepository
{
    Task<IReadOnlyList<Candle>> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task SaveCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default);
    Task<DateTimeOffset?> GetLatestTimestampAsync(int symbolId, Timeframe timeframe, CancellationToken ct = default);
}

public interface IDataQualityFlagRepository
{
    Task SaveFlagAsync(DataQualityFlag flag, CancellationToken ct = default);
    Task<IReadOnlyList<DataQualityFlag>> GetFlagsAsync(int symbolId, DateOnly from, DateOnly to, CancellationToken ct = default);
}

public interface IIndicatorValueRepository
{
    Task SaveAsync(IndicatorSnapshot snapshot, CancellationToken ct = default);
    Task SaveBatchAsync(IReadOnlyList<IndicatorSnapshot> snapshots, CancellationToken ct = default);
    Task<IReadOnlyList<IndicatorSnapshot>> GetAsync(int symbolId, Timeframe timeframe, string indicatorKey, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

public interface IMarketStructureEventRepository
{
    Task SaveAsync(MarketStructureEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<MarketStructureEvent>> GetAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

public interface IAnalysisRepository
{
    Task<int> SaveAsync(AnalysisResult result, CancellationToken ct = default);
    Task SaveTradeSignalAsync(int analysisId, TradeSignal signal, CancellationToken ct = default);
    Task<IReadOnlyList<AnalysisResult>> GetForDateAsync(DateOnly asOfDate, CancellationToken ct = default);
}

/// <summary>
/// One completed scan run's headline counts and timings. The ScanHistory table
/// (§18) existed in the schema from the first migration but nothing ever wrote
/// to it — the §23/§25 benchmark deliverable ("Stage-1/Stage-2 timings reported
/// separately") had no history to report from beyond whatever a single run's
/// console output showed. UniverseScanner.RunAsync now saves one row here per
/// completed run.
/// </summary>
public sealed record ScanHistoryRecord(
    int ScanId,
    DateTimeOffset RunAt,
    int Stage1Count,
    int Stage2Count,
    long Stage1DurationMs,
    long Stage2DurationMs);

public interface IScanHistoryRepository
{
    Task<int> SaveAsync(ScanHistoryRecord record, CancellationToken ct = default);

    /// <summary>Most recent runs first. Used by the `dotnet run -- benchmark` CLI command and the dashboard's benchmark view.</summary>
    Task<IReadOnlyList<ScanHistoryRecord>> GetRecentAsync(int count, CancellationToken ct = default);
}
