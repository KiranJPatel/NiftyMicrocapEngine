using System.Globalization;
using Dapper;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;
using NiftyMicrocapEngine.Infrastructure.Persistence.Resilience;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

public sealed class SqliteIndicatorValueRepository : IIndicatorValueRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    public SqliteIndicatorValueRepository(ISqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    private const string UpsertSql = """
        INSERT INTO IndicatorValues (SymbolId, Timeframe, Timestamp, IndicatorKey, Value, SignalState)
        VALUES (@SymbolId, @Timeframe, @Timestamp, @IndicatorKey, @Value, @SignalState)
        ON CONFLICT (SymbolId, Timeframe, Timestamp, IndicatorKey) DO UPDATE SET
            Value = excluded.Value, SignalState = excluded.SignalState;
        """;

    public Task SaveAsync(IndicatorSnapshot snapshot, CancellationToken ct = default) => SqliteRetry.ExecuteAsync(async () =>
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(UpsertSql, ToParams(snapshot), cancellationToken: ct));
    });

    public Task SaveBatchAsync(IReadOnlyList<IndicatorSnapshot> snapshots, CancellationToken ct = default)
    {
        if (snapshots.Count == 0) return Task.CompletedTask;

        return SqliteRetry.ExecuteAsync(async () =>
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(ct);
            using var transaction = connection.BeginTransaction();

            foreach (var snapshot in snapshots)
            {
                await connection.ExecuteAsync(new CommandDefinition(UpsertSql, ToParams(snapshot), transaction, cancellationToken: ct));
            }

            transaction.Commit();
        });
    }

    public async Task<IReadOnlyList<IndicatorSnapshot>> GetAsync(int symbolId, Timeframe timeframe, string indicatorKey, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT SymbolId, Timeframe, Timestamp, IndicatorKey, Value, SignalState
            FROM IndicatorValues
            WHERE SymbolId = @SymbolId AND Timeframe = @Timeframe AND IndicatorKey = @IndicatorKey
              AND Timestamp >= @From AND Timestamp <= @To
            ORDER BY Timestamp ASC;
            """;

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(sql, new
        {
            SymbolId = symbolId,
            Timeframe = timeframe.ToString(),
            IndicatorKey = indicatorKey,
            From = from.ToString("O"),
            To = to.ToString("O")
        }, cancellationToken: ct));

        return rows.Select(r => new IndicatorSnapshot(
            r.SymbolId, Enum.Parse<Timeframe>(r.Timeframe), DateTimeOffset.Parse(r.Timestamp, CultureInfo.InvariantCulture),
            r.IndicatorKey, r.Value is null ? null : (decimal)r.Value.Value, r.SignalState)).ToList();
    }

    private static object ToParams(IndicatorSnapshot s) => new
    {
        s.SymbolId,
        Timeframe = s.Timeframe.ToString(),
        Timestamp = s.Timestamp.ToString("O"),
        s.IndicatorKey,
        Value = s.Value is null ? null : (double?)s.Value.Value,
        s.SignalState
    };

    private sealed class Row
    {
        public int SymbolId { get; set; }
        public string Timeframe { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string IndicatorKey { get; set; } = "";
        public double? Value { get; set; }
        public string? SignalState { get; set; }
    }
}
