namespace NiftyMicrocapEngine.Domain;

/// <summary>Health state of an indicator's current value — referenced by IIndicator (§7).</summary>
public enum IndicatorHealth { OK, InsufficientData, Stale }

/// <summary>
/// A point-in-time universe membership snapshot (§6.5) — versioned, never a single
/// live-mutable list, so backtests replaying an older date use the constituent list
/// that was actually in force then rather than today's list (survivorship bias).
/// </summary>
public sealed record UniverseSnapshot(int UniverseSnapshotId, DateOnly AsOfDate, DateTimeOffset FetchedAtUtc);

public sealed record UniverseSnapshotMember(int UniverseSnapshotId, int SymbolId);
