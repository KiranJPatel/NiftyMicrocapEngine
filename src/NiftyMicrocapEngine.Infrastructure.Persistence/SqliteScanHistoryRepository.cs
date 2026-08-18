using System.Globalization;
using Dapper;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Infrastructure.Persistence.Resilience;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

/// <summary>
/// Persists to the ScanHistory table defined in §18's schema (present since the
/// first migration but previously never written to — see ScanHistoryRecord's
/// doc comment). RunAt is stored as an ISO-8601 string with offset, matching
/// this codebase's existing convention for DateTimeOffset columns (see
/// SqliteCandleRepository's Timestamp handling) rather than the yyyy-MM-dd
/// date-only format used for AsOfDate columns elsewhere.
/// </summary>
public sealed class SqliteScanHistoryRepository : IScanHistoryRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    public SqliteScanHistoryRepository(ISqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public Task<int> SaveAsync(ScanHistoryRecord record, CancellationToken ct = default) => SqliteRetry.ExecuteAsync(async () =>
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            INSERT INTO ScanHistory (RunAt, Stage1Count, Stage2Count, Stage1DurationMs, Stage2DurationMs)
            VALUES (@RunAt, @Stage1Count, @Stage2Count, @Stage1DurationMs, @Stage2DurationMs)
            RETURNING ScanId;
            """;

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
        {
            RunAt = record.RunAt.ToString("O"),
            record.Stage1Count,
            record.Stage2Count,
            record.Stage1DurationMs,
            record.Stage2DurationMs
        }, cancellationToken: ct));
    });

    public async Task<IReadOnlyList<ScanHistoryRecord>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT ScanId, RunAt, Stage1Count, Stage2Count, Stage1DurationMs, Stage2DurationMs
            FROM ScanHistory ORDER BY ScanId DESC LIMIT @Count;
            """;

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(sql, new { Count = count }, cancellationToken: ct));

        return rows.Select(r => new ScanHistoryRecord(
            r.ScanId, DateTimeOffset.Parse(r.RunAt, CultureInfo.InvariantCulture), r.Stage1Count, r.Stage2Count, r.Stage1DurationMs, r.Stage2DurationMs)).ToList();
    }

    private sealed class Row
    {
        public int ScanId { get; set; }
        public string RunAt { get; set; } = "";
        public int Stage1Count { get; set; }
        public int Stage2Count { get; set; }
        public long Stage1DurationMs { get; set; }
        public long Stage2DurationMs { get; set; }
    }
}
