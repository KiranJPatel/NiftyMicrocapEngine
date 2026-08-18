using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NiftyMicrocapEngine.Infrastructure.Persistence;
using Xunit;

namespace NiftyMicrocapEngine.Infrastructure.Tests.Persistence;

public class MigrationRunnerTests
{
    [Fact]
    public async Task ApplyMigrationsAsync_CreatesExpectedTables()
    {
        var connectionString = $"Data Source=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        using var keepAliveConnection = new SqliteConnection(connectionString);
        await keepAliveConnection.OpenAsync();

        var runner = new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyMigrationsAsync();

        var tableNames = await GetTableNamesAsync(keepAliveConnection);

        Assert.Contains("Symbols", tableNames);
        Assert.Contains("SymbolMapping", tableNames);
        Assert.Contains("UniverseSnapshots", tableNames);
        Assert.Contains("UniverseSnapshotMembers", tableNames);
        Assert.Contains("Candles", tableNames);
        Assert.Contains("DataQualityFlags", tableNames);
        Assert.Contains("IndicatorValues", tableNames);
        Assert.Contains("MarketStructureEvents", tableNames);
        Assert.Contains("Analysis", tableNames);
        Assert.Contains("TradeSignals", tableNames);
        Assert.Contains("ScanHistory", tableNames);
        Assert.Contains("SchemaMigrations", tableNames);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_RecordsAppliedMigrationInTrackingTable()
    {
        var connectionString = $"Data Source=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        using var keepAliveConnection = new SqliteConnection(connectionString);
        await keepAliveConnection.OpenAsync();

        var runner = new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyMigrationsAsync();

        using var command = keepAliveConnection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM SchemaMigrations;";
        using var reader = await command.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));

        Assert.Contains("001_InitialSchema", ids);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_CalledTwice_IsIdempotent()
    {
        var connectionString = $"Data Source=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        using var keepAliveConnection = new SqliteConnection(connectionString);
        await keepAliveConnection.OpenAsync();

        var runner = new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance);

        await runner.ApplyMigrationsAsync();
        var exception = await Record.ExceptionAsync(() => runner.ApplyMigrationsAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task ApplyMigrationsAsync_CandlesTable_EnforcesCompositePrimaryKey()
    {
        var connectionString = $"Data Source=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        using var keepAliveConnection = new SqliteConnection(connectionString);
        await keepAliveConnection.OpenAsync();

        var runner = new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyMigrationsAsync();

        // Symbols has no FK enforcement issue here since SQLite FKs are off by
        // default unless PRAGMA foreign_keys=ON is set per-connection — the
        // migration SQL sets it, but Dapper/raw ADO connections in this test
        // don't inherit that pragma automatically, so insert the parent row too.
        using (var symbolCmd = keepAliveConnection.CreateCommand())
        {
            symbolCmd.CommandText = "INSERT INTO Symbols (SymbolId, NseSymbol, CompanyName, Sector, IsActive) VALUES (1, 'RELIANCE', 'Reliance Industries', 'Energy', 1);";
            await symbolCmd.ExecuteNonQueryAsync();
        }

        async Task InsertCandleAsync()
        {
            using var command = keepAliveConnection.CreateCommand();
            command.CommandText = """
                INSERT INTO Candles (SymbolId, Timeframe, Timestamp, Open, High, Low, Close, AdjClose, Volume)
                VALUES (1, 'Daily', '2026-01-01T00:00:00Z', 100, 105, 99, 102, 102, 10000);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await InsertCandleAsync();
        await Assert.ThrowsAsync<SqliteException>(InsertCandleAsync);
    }

    private static async Task<List<string>> GetTableNamesAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }
}
