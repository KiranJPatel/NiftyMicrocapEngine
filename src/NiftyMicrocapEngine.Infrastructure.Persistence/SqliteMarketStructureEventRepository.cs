using System.Globalization;
using Dapper;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;
using NiftyMicrocapEngine.Infrastructure.Persistence.Resilience;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

public sealed class SqliteMarketStructureEventRepository : IMarketStructureEventRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    public SqliteMarketStructureEventRepository(ISqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public Task SaveAsync(MarketStructureEvent evt, CancellationToken ct = default) => SqliteRetry.ExecuteAsync(async () =>
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            INSERT INTO MarketStructureEvents (SymbolId, Timeframe, Timestamp, EventType, Detail)
            VALUES (@SymbolId, @Timeframe, @Timestamp, @EventType, @Detail);
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            evt.SymbolId,
            Timeframe = evt.Timeframe.ToString(),
            Timestamp = evt.Timestamp.ToString("O"),
            EventType = evt.EventType.ToString(),
            evt.Detail
        }, cancellationToken: ct));
    });

    public async Task<IReadOnlyList<MarketStructureEvent>> GetAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT SymbolId, Timeframe, Timestamp, EventType, Detail FROM MarketStructureEvents
            WHERE SymbolId = @SymbolId AND Timeframe = @Timeframe AND Timestamp >= @From AND Timestamp <= @To
            ORDER BY Timestamp ASC;
            """;

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(sql, new
        {
            SymbolId = symbolId,
            Timeframe = timeframe.ToString(),
            From = from.ToString("O"),
            To = to.ToString("O")
        }, cancellationToken: ct));

        return rows.Select(r => new MarketStructureEvent(
            r.SymbolId, Enum.Parse<Timeframe>(r.Timeframe), DateTimeOffset.Parse(r.Timestamp, CultureInfo.InvariantCulture),
            Enum.Parse<StructureEventType>(r.EventType), r.Detail)).ToList();
    }

    private sealed class Row
    {
        public int SymbolId { get; set; }
        public string Timeframe { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public string EventType { get; set; } = "";
        public string? Detail { get; set; }
    }
}
