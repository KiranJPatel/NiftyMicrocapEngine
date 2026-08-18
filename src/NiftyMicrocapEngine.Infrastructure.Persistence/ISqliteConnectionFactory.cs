using Microsoft.Data.Sqlite;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

public interface ISqliteConnectionFactory
{
    SqliteConnection CreateConnection();
}

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public SqliteConnection CreateConnection() => new(_connectionString);
}
