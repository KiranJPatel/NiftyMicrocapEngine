using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Tests.Scanning;

public sealed class FakeIndicatorValueRepository : IIndicatorValueRepository
{
    public List<IndicatorSnapshot> Saved { get; } = new();

    public Task SaveAsync(IndicatorSnapshot snapshot, CancellationToken ct = default)
    {
        Saved.Add(snapshot);
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync(IReadOnlyList<IndicatorSnapshot> snapshots, CancellationToken ct = default)
    {
        Saved.AddRange(snapshots);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IndicatorSnapshot>> GetAsync(int symbolId, Timeframe timeframe, string indicatorKey, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IndicatorSnapshot>>(Saved.Where(s => s.SymbolId == symbolId && s.Timeframe == timeframe && s.IndicatorKey == indicatorKey).ToList());
}

public sealed class FakeMarketStructureEventRepository : IMarketStructureEventRepository
{
    public List<MarketStructureEvent> Saved { get; } = new();

    public Task SaveAsync(MarketStructureEvent evt, CancellationToken ct = default)
    {
        Saved.Add(evt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MarketStructureEvent>> GetAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MarketStructureEvent>>(Saved.Where(e => e.SymbolId == symbolId && e.Timeframe == timeframe).ToList());
}

/// <summary>Added alongside IScanHistoryRepository (ScanHistory persistence fix) so existing UniverseScanner test construction keeps compiling — see UniverseScanner.cs's new constructor parameter.</summary>
public sealed class FakeScanHistoryRepository : IScanHistoryRepository
{
    public List<ScanHistoryRecord> Saved { get; } = new();

    public Task<int> SaveAsync(ScanHistoryRecord record, CancellationToken ct = default)
    {
        Saved.Add(record);
        return Task.FromResult(Saved.Count);
    }

    public Task<IReadOnlyList<ScanHistoryRecord>> GetRecentAsync(int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ScanHistoryRecord>>(Saved.OrderByDescending(r => r.ScanId).Take(count).ToList());
}

/// <summary>Added alongside INseCircuitBandProvider (§6.8's real circuit-band feed) so existing UniverseScanner test construction keeps compiling. Empty by default, matching "feed unavailable" — existing tests exercise the zero-range-only fallback path exactly as before this feed existed.</summary>
public sealed class FakeNseCircuitBandProvider : NiftyMicrocapEngine.Application.DataQuality.INseCircuitBandProvider
{
    private readonly IReadOnlyDictionary<string, decimal> _bands;

    public FakeNseCircuitBandProvider(IReadOnlyDictionary<string, decimal>? bands = null) => _bands = bands ?? new Dictionary<string, decimal>();

    public Task<IReadOnlyDictionary<string, decimal>> GetCircuitBandsAsync(CancellationToken ct = default) => Task.FromResult(_bands);
}
