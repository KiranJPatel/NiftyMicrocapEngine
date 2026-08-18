using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

/// <summary>
/// Applies embedded .sql migration files in filename order, tracking applied
/// migrations in SchemaMigrations so re-runs are idempotent.
/// </summary>
public sealed class MigrationRunner
{
    private readonly string _connectionString;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(string connectionString, ILogger<MigrationRunner> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureMigrationsTableAsync(connection, cancellationToken);
        var applied = await GetAppliedMigrationIdsAsync(connection, cancellationToken);

        var assembly = Assembly.GetExecutingAssembly();
        var migrationResourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("Migrations.") && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var resourceName in migrationResourceNames)
        {
            var migrationId = ExtractMigrationId(resourceName);
            if (applied.Contains(migrationId))
            {
                _logger.LogDebug("Migration {MigrationId} already applied, skipping.", migrationId);
                continue;
            }

            _logger.LogInformation("Applying migration {MigrationId}...", migrationId);

            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded migration resource {resourceName} could not be opened.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken);

            await using var transaction = connection.BeginTransaction();
            try
            {
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var recordCommand = connection.CreateCommand())
                {
                    recordCommand.Transaction = transaction;
                    recordCommand.CommandText =
                        "INSERT INTO SchemaMigrations (MigrationId, AppliedAtUtc) VALUES ($id, $appliedAt);";
                    recordCommand.Parameters.AddWithValue("$id", migrationId);
                    recordCommand.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
                    await recordCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                transaction.Commit();
                _logger.LogInformation("Migration {MigrationId} applied successfully.", migrationId);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    private static async Task EnsureMigrationsTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS SchemaMigrations (MigrationId TEXT NOT NULL PRIMARY KEY, AppliedAtUtc TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> GetAppliedMigrationIdsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM SchemaMigrations;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static string ExtractMigrationId(string resourceName)
    {
        var afterMigrations = resourceName[(resourceName.IndexOf("Migrations.", StringComparison.Ordinal) + "Migrations.".Length)..];
        return afterMigrations.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            ? afterMigrations[..^4]
            : afterMigrations;
    }
}
