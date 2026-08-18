using System.Globalization;
using Dapper;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;
using NiftyMicrocapEngine.Infrastructure.Persistence.Resilience;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

public sealed class SqliteUniverseRepository : IUniverseRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteUniverseRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<UniverseSnapshot?> GetLatestSnapshotAsync(CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT SnapshotId, EffectiveDate, IndexName, SourceDocument
            FROM UniverseSnapshots ORDER BY EffectiveDate DESC LIMIT 1;
            """;

        var row = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(sql, cancellationToken: ct));
        if (row is null) return null;

        // FetchedAtUtc isn't a column in the §18 schema (only EffectiveDate is) — we
        // report it as the EffectiveDate at midnight UTC rather than inventing a
        // separate fetched-at concept the schema doesn't track.
        return new UniverseSnapshot(row.SnapshotId, DateOnly.Parse(row.EffectiveDate, CultureInfo.InvariantCulture),
            new DateTimeOffset(DateOnly.Parse(row.EffectiveDate, CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    public Task<int> SaveSnapshotAsync(UniverseSnapshot snapshot, IReadOnlyList<int> memberSymbolIds, CancellationToken ct = default) => SqliteRetry.ExecuteAsync(async () =>
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();

        const string insertSnapshotSql = """
            INSERT INTO UniverseSnapshots (EffectiveDate, IndexName, SourceDocument)
            VALUES (@EffectiveDate, 'NIFTY_MICROCAP_250', NULL)
            RETURNING SnapshotId;
            """;

        var snapshotId = await connection.ExecuteScalarAsync<int>(new CommandDefinition(insertSnapshotSql, new
        {
            EffectiveDate = snapshot.AsOfDate.ToString("yyyy-MM-dd")
        }, transaction, cancellationToken: ct));

        const string insertMemberSql = """
            INSERT INTO UniverseSnapshotMembers (SnapshotId, SymbolId) VALUES (@SnapshotId, @SymbolId);
            """;

        foreach (var symbolId in memberSymbolIds)
        {
            await connection.ExecuteAsync(new CommandDefinition(insertMemberSql, new { SnapshotId = snapshotId, SymbolId = symbolId }, transaction, cancellationToken: ct));
        }

        transaction.Commit();
        return snapshotId;
    });

    public async Task<IReadOnlyList<int>> GetMemberSymbolIdsAsync(int snapshotId, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = "SELECT SymbolId FROM UniverseSnapshotMembers WHERE SnapshotId = @SnapshotId;";
        var rows = await connection.QueryAsync<int>(new CommandDefinition(sql, new { SnapshotId = snapshotId }, cancellationToken: ct));
        return rows.ToList();
    }

    private sealed class SnapshotRow
    {
        public int SnapshotId { get; set; }
        public string EffectiveDate { get; set; } = "";
        public string IndexName { get; set; } = "";
        public string? SourceDocument { get; set; }
    }
}
