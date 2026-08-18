using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NiftyMicrocapEngine.Domain;
using NiftyMicrocapEngine.Infrastructure.Persistence;
using Xunit;

namespace NiftyMicrocapEngine.Infrastructure.Tests.Persistence;

/// <summary>
/// §21's no-repaint regression suite, at the layer where a real bug was
/// found this pass (see DateOnlyExtensions.ToUtcDateTimeOffset's doc
/// comment): SqliteCandleRepository stores and compares Timestamp as
/// ISO-8601 TEXT via plain SQL >= / &lt;=, a lexicographic comparison. Every
/// call site that builds a query boundary from a DateOnly used to do so via
/// the DateTime→DateTimeOffset implicit conversion, which silently picks up
/// the RUNNING PROCESS'S LOCAL TIMEZONE offset for Unspecified-Kind
/// DateTimes — invisible on a UTC-configured deployment machine (where
/// Local happens to equal +00:00), which is exactly why this could ship
/// unnoticed. These tests seed a symbol with candles that include some AFTER
/// a requested `to` boundary and assert none of them leak into the result —
/// the literal definition of "querying as of date X must never see data
/// beyond X."
/// </summary>
public class SqliteCandleRepositoryNoRepaintTests
{
    private static async Task<(SqliteConnection KeepAlive, SqliteCandleRepository Repository)> CreateRepositoryAsync()
    {
        var connectionString = $"Data Source=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var runner = new MigrationRunner(connectionString, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyMigrationsAsync();

        using (var symbolCmd = keepAlive.CreateCommand())
        {
            symbolCmd.CommandText = "INSERT INTO Symbols (SymbolId, NseSymbol, CompanyName, Sector, IsActive) VALUES (1, 'TESTCO', 'Test Company', 'IT', 1);";
            await symbolCmd.ExecuteNonQueryAsync();
        }

        var repository = new SqliteCandleRepository(new SqliteConnectionFactory(connectionString));
        return (keepAlive, repository);
    }

    private static async Task InsertCandleAsync(SqliteConnection connection, DateTimeOffset timestamp, decimal close)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Candles (SymbolId, Timeframe, Timestamp, Open, High, Low, Close, AdjClose, Volume)
            VALUES (1, 'Daily', $Timestamp, $Close, $Close, $Close, $Close, $Close, 10000);
            """;
        command.Parameters.AddWithValue("$Timestamp", timestamp.ToString("O"));
        command.Parameters.AddWithValue("$Close", (double)close);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task GetCandlesAsync_ToBoundaryBuiltViaTheFixedExtension_ExcludesCandlesAfterIt()
    {
        var (keepAlive, repository) = await CreateRepositoryAsync();
        using var _ = keepAlive;

        // Candles are stored the way the real provider actually stores them
        // — DateTimeOffset.FromUnixTimeSeconds is always +00:00 (see
        // YahooFinanceMarketDataProvider) — one INSIDE the requested window,
        // one clearly AFTER it (simulating "more data exists than the
        // as-of-date query should see").
        var asOfDate = new DateOnly(2026, 1, 15);
        await InsertCandleAsync(keepAlive, new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), 100m);
        await InsertCandleAsync(keepAlive, new DateTimeOffset(2026, 1, 20, 0, 0, 0, TimeSpan.Zero), 999m); // 5 days AFTER asOfDate — must never appear

        var from = asOfDate.AddYears(-1).ToUtcDateTimeOffset(TimeOnly.MinValue);
        var to = asOfDate.ToUtcDateTimeOffset(TimeOnly.MaxValue);

        var result = await repository.GetCandlesAsync(1, Timeframe.Daily, from, to);

        Assert.Single(result);
        Assert.Equal(100m, result[0].Close);
    }

    [Fact]
    public async Task GetCandlesAsync_BoundaryBuiltViaTheOldBuggyPattern_WouldHaveFailedOnANonUtcMachine()
    {
        // This test documents the bug itself, not just the fix: it builds a
        // boundary using an EXPLICIT non-UTC offset (simulating what the old
        // `asOfDate.ToDateTime(TimeOnly.MaxValue)` pattern silently produced
        // on any machine whose local timezone isn't UTC) and shows it sorts
        // WRONG relative to the +00:00-stored candles — the exact failure
        // mode ToUtcDateTimeOffset exists to prevent. If this assertion ever
        // starts failing, DateOnlyExtensions.ToUtcDateTimeOffset (or the
        // storage format it protects against) has changed in a way that
        // needs re-auditing, not just a test update.
        var (keepAlive, repository) = await CreateRepositoryAsync();
        using var _ = keepAlive;

        await InsertCandleAsync(keepAlive, new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), 100m);

        // +05:30 (IST) "midnight Jan 15 IST" is 2026-01-14T18:30:00 UTC —
        // chronologically BEFORE the stored +00:00 candle above — but its
        // ISO-8601 string ("...T00:00:00+05:30") sorts LEXICOGRAPHICALLY
        // AFTER "...T00:00:00+00:00" because '5' > '0' at the offset-hour
        // digit, with everything before it identical.
        var buggyTo = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.FromHours(5.5));
        var from = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await repository.GetCandlesAsync(1, Timeframe.Daily, from, buggyTo);

        // The lexicographic (mis)comparison happens to still include this
        // particular candle here (the string ordering error runs in the
        // "includes something it shouldn't have excluded" direction for
        // this example, not the reverse) — the point isn't this specific
        // outcome, it's that string comparison across mixed offsets is
        // unpredictable at all, which is exactly why every call site now
        // goes through ToUtcDateTimeOffset instead of ad-hoc offsets.
        Assert.Single(result);
    }
}
