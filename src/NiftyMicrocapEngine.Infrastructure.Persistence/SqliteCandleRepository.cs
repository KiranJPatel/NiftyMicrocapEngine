using System.Globalization;
using Dapper;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;
using NiftyMicrocapEngine.Infrastructure.Persistence.Resilience;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

public sealed class SqliteCandleRepository : ICandleRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteCandleRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT SymbolId, Timeframe, Timestamp, Open, High, Low, Close, AdjClose, Volume
            FROM Candles
            WHERE SymbolId = @SymbolId AND Timeframe = @Timeframe
              AND Timestamp >= @From AND Timestamp <= @To
            ORDER BY Timestamp ASC;
            """;

        var rows = await connection.QueryAsync<CandleRow>(new CommandDefinition(sql, new
        {
            SymbolId = symbolId,
            Timeframe = timeframe.ToString(),
            From = from.ToString("O"),
            To = to.ToString("O")
        }, cancellationToken: ct));

        return rows.Select(r => r.ToDomain()).ToList();
    }

    public Task SaveCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken ct = default)
    {
        if (candles.Count == 0) return Task.CompletedTask;

        return SqliteRetry.ExecuteAsync(async () =>
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(ct);
            using var transaction = connection.BeginTransaction();

            const string upsertSql = """
                INSERT INTO Candles (SymbolId, Timeframe, Timestamp, Open, High, Low, Close, AdjClose, Volume)
                VALUES (@SymbolId, @Timeframe, @Timestamp, @Open, @High, @Low, @Close, @AdjClose, @Volume)
                ON CONFLICT (SymbolId, Timeframe, Timestamp) DO UPDATE SET
                    Open = excluded.Open, High = excluded.High, Low = excluded.Low,
                    Close = excluded.Close, AdjClose = excluded.AdjClose, Volume = excluded.Volume;
                """;

            foreach (var candle in candles)
            {
                await connection.ExecuteAsync(new CommandDefinition(upsertSql, new
                {
                    candle.SymbolId,
                    Timeframe = candle.Timeframe.ToString(),
                    Timestamp = candle.Timestamp.ToString("O"),
                    Open = (double)candle.Open,
                    High = (double)candle.High,
                    Low = (double)candle.Low,
                    Close = (double)candle.Close,
                    AdjClose = (double)candle.AdjClose,
                    candle.Volume
                }, transaction, cancellationToken: ct));
            }

            transaction.Commit();
        });
    }

    public async Task<DateTimeOffset?> GetLatestTimestampAsync(int symbolId, Timeframe timeframe, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = "SELECT MAX(Timestamp) FROM Candles WHERE SymbolId = @SymbolId AND Timeframe = @Timeframe;";
        var result = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(sql, new
        {
            SymbolId = symbolId,
            Timeframe = timeframe.ToString()
        }, cancellationToken: ct));

        return result is null ? null : DateTimeOffset.Parse(result, CultureInfo.InvariantCulture);
    }

    // NOTE: schema stores Open/High/Low/Close/AdjClose as SQLite REAL (i.e. double),
    // not as arbitrary-precision decimal — per §18's literal column types. This is a
    // deliberate spec choice (REAL, not TEXT-encoded decimal like an earlier draft of
    // this codebase used) and means a full IEEE-754 double round-trip, not exact
    // decimal round-trip, for stored prices. Acceptable for NSE equity price
    // precision (2 decimal places) but worth flagging since it differs from the
    // higher-precision TEXT-decimal pattern used elsewhere in prior drafts.
    private sealed class CandleRow
    {
        public int SymbolId { get; set; }
        public string Timeframe { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public double AdjClose { get; set; }
        public long Volume { get; set; }

        public Candle ToDomain() => new(
            SymbolId,
            Enum.Parse<Timeframe>(Timeframe),
            DateTimeOffset.Parse(Timestamp, CultureInfo.InvariantCulture),
            (decimal)Open, (decimal)High, (decimal)Low, (decimal)Close, (decimal)AdjClose, Volume);
    }
}
