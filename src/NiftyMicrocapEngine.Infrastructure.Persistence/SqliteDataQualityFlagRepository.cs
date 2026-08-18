using System.Globalization;
using Dapper;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;
using NiftyMicrocapEngine.Infrastructure.Persistence.Resilience;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

public sealed class SqliteDataQualityFlagRepository : IDataQualityFlagRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    public SqliteDataQualityFlagRepository(ISqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public Task SaveFlagAsync(DataQualityFlag flag, CancellationToken ct = default) => SqliteRetry.ExecuteAsync(async () =>
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            INSERT INTO DataQualityFlags (SymbolId, AsOfDate, FlagType, Detail)
            VALUES (@SymbolId, @AsOfDate, @FlagType, @Detail)
            ON CONFLICT (SymbolId, AsOfDate, FlagType) DO UPDATE SET Detail = excluded.Detail;
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            flag.SymbolId,
            AsOfDate = flag.AsOfDate.ToString("yyyy-MM-dd"),
            flag.FlagType,
            flag.Detail
        }, cancellationToken: ct));
    });

    public async Task<IReadOnlyList<DataQualityFlag>> GetFlagsAsync(int symbolId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT SymbolId, AsOfDate, FlagType, Detail FROM DataQualityFlags
            WHERE SymbolId = @SymbolId AND AsOfDate >= @From AND AsOfDate <= @To;
            """;

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(sql, new
        {
            SymbolId = symbolId,
            From = from.ToString("yyyy-MM-dd"),
            To = to.ToString("yyyy-MM-dd")
        }, cancellationToken: ct));

        return rows.Select(r => new DataQualityFlag(r.SymbolId, DateOnly.Parse(r.AsOfDate, CultureInfo.InvariantCulture), r.FlagType, r.Detail)).ToList();
    }

    private sealed class Row
    {
        public int SymbolId { get; set; }
        public string AsOfDate { get; set; } = "";
        public string FlagType { get; set; } = "";
        public string? Detail { get; set; }
    }
}
