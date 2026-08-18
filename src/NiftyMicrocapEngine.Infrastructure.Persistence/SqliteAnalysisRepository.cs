using System.Globalization;
using Dapper;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;
using NiftyMicrocapEngine.Infrastructure.Persistence.Resilience;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

public sealed class SqliteAnalysisRepository : IAnalysisRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    public SqliteAnalysisRepository(ISqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public Task<int> SaveAsync(AnalysisResult result, CancellationToken ct = default) => SqliteRetry.ExecuteAsync(async () =>
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            INSERT INTO Analysis (SymbolId, AsOfDate, Decision, Confidence, LayerScoresJson, ReasoningText, HardGateFailed)
            VALUES (@SymbolId, @AsOfDate, @Decision, @Confidence, @LayerScoresJson, @ReasoningText, @HardGateFailed)
            RETURNING AnalysisId;
            """;

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
        {
            result.SymbolId,
            AsOfDate = result.AsOfDate.ToString("yyyy-MM-dd"),
            result.Decision,
            Confidence = (double)result.Confidence,
            result.LayerScoresJson,
            result.ReasoningText,
            result.HardGateFailed
        }, cancellationToken: ct));
    });

    public Task SaveTradeSignalAsync(int analysisId, TradeSignal signal, CancellationToken ct = default) => SqliteRetry.ExecuteAsync(async () =>
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            INSERT INTO TradeSignals (AnalysisId, Entry, StopLoss, Target1, Target2, Target3, RiskPercent, RiskRewardRatio, InvalidationLevel)
            VALUES (@AnalysisId, @Entry, @StopLoss, @Target1, @Target2, @Target3, @RiskPercent, @RiskRewardRatio, @InvalidationLevel);
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            AnalysisId = analysisId,
            Entry = (double)signal.Entry,
            StopLoss = (double)signal.StopLoss,
            Target1 = (double)signal.Target1,
            Target2 = (double)signal.Target2,
            Target3 = (double)signal.Target3,
            RiskPercent = (double)signal.RiskPercent,
            RiskRewardRatio = (double)signal.RiskRewardRatio,
            signal.InvalidationLevel
        }, cancellationToken: ct));
    });

    public async Task<IReadOnlyList<AnalysisResult>> GetForDateAsync(DateOnly asOfDate, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT SymbolId, AsOfDate, Decision, Confidence, LayerScoresJson, ReasoningText, HardGateFailed
            FROM Analysis WHERE AsOfDate = @AsOfDate ORDER BY Confidence DESC;
            """;

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(sql, new { AsOfDate = asOfDate.ToString("yyyy-MM-dd") }, cancellationToken: ct));

        return rows.Select(r => new AnalysisResult(
            r.SymbolId, DateOnly.Parse(r.AsOfDate, CultureInfo.InvariantCulture), r.Decision,
            (decimal)r.Confidence, r.LayerScoresJson, r.ReasoningText, r.HardGateFailed)).ToList();
    }

    private sealed class Row
    {
        public int SymbolId { get; set; }
        public string AsOfDate { get; set; } = "";
        public string Decision { get; set; } = "";
        public double Confidence { get; set; }
        public string LayerScoresJson { get; set; } = "";
        public string ReasoningText { get; set; } = "";
        public string? HardGateFailed { get; set; }
    }
}
