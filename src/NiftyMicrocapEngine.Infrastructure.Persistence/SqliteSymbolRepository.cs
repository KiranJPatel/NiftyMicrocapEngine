using System.Globalization;
using Dapper;
using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;
using NiftyMicrocapEngine.Infrastructure.Persistence.Resilience;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

public sealed class SqliteSymbolRepository : ISymbolRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteSymbolRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Symbol?> GetBySymbolIdAsync(int symbolId, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = "SELECT SymbolId, NseSymbol, CompanyName, Sector, IsActive FROM Symbols WHERE SymbolId = @SymbolId;";
        var row = await connection.QuerySingleOrDefaultAsync<SymbolRow>(new CommandDefinition(sql, new { SymbolId = symbolId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<Symbol?> GetByNseSymbolAsync(string nseSymbol, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = "SELECT SymbolId, NseSymbol, CompanyName, Sector, IsActive FROM Symbols WHERE NseSymbol = @NseSymbol;";
        var row = await connection.QuerySingleOrDefaultAsync<SymbolRow>(new CommandDefinition(sql, new { NseSymbol = nseSymbol }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Symbol>> GetAllActiveAsync(CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = "SELECT SymbolId, NseSymbol, CompanyName, Sector, IsActive FROM Symbols WHERE IsActive = 1 ORDER BY NseSymbol;";
        var rows = await connection.QueryAsync<SymbolRow>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public Task<int> UpsertAsync(Symbol symbol, CancellationToken ct = default) => SqliteRetry.ExecuteAsync(async () =>
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            INSERT INTO Symbols (SymbolId, NseSymbol, CompanyName, Sector, IsActive)
            VALUES (@SymbolId, @NseSymbol, @CompanyName, @Sector, @IsActive)
            ON CONFLICT (SymbolId) DO UPDATE SET
                NseSymbol = excluded.NseSymbol,
                CompanyName = excluded.CompanyName,
                Sector = excluded.Sector,
                IsActive = excluded.IsActive
            RETURNING SymbolId;
            """;

        // SQLite's INTEGER PRIMARY KEY auto-assigns when SymbolId is 0/omitted; here
        // we always pass an explicit value since callers (universe sync) resolve or
        // mint SymbolIds themselves — see the "NULL means autoincrement" SQLite rule,
        // which we deliberately avoid relying on for a business key like this.
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new
        {
            symbol.SymbolId,
            symbol.NseSymbol,
            symbol.CompanyName,
            symbol.Sector,
            IsActive = symbol.IsActive ? 1 : 0
        }, cancellationToken: ct));
    });

    public Task SaveMappingAsync(SymbolMapping mapping, CancellationToken ct = default) => SqliteRetry.ExecuteAsync(async () =>
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            INSERT INTO SymbolMapping (SymbolId, Provider, ExternalId, EffectiveFrom, EffectiveTo)
            VALUES (@SymbolId, @Provider, @ExternalId, @EffectiveFrom, @EffectiveTo);
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            mapping.SymbolId,
            Provider = mapping.Provider.ToString(),
            mapping.ExternalId,
            EffectiveFrom = mapping.EffectiveFrom.ToString("yyyy-MM-dd"),
            EffectiveTo = mapping.EffectiveTo?.ToString("yyyy-MM-dd")
        }, cancellationToken: ct));
    });

    public async Task<SymbolMapping?> GetActiveMappingAsync(int symbolId, DataProviderKind provider, DateOnly asOf, CancellationToken ct = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(ct);

        const string sql = """
            SELECT SymbolId, Provider, ExternalId, EffectiveFrom, EffectiveTo
            FROM SymbolMapping
            WHERE SymbolId = @SymbolId AND Provider = @Provider
              AND EffectiveFrom <= @AsOf AND (EffectiveTo IS NULL OR EffectiveTo >= @AsOf)
            ORDER BY EffectiveFrom DESC LIMIT 1;
            """;

        var row = await connection.QuerySingleOrDefaultAsync<MappingRow>(new CommandDefinition(sql, new
        {
            SymbolId = symbolId,
            Provider = provider.ToString(),
            AsOf = asOf.ToString("yyyy-MM-dd")
        }, cancellationToken: ct));

        return row?.ToDomain();
    }

    private sealed class SymbolRow
    {
        public int SymbolId { get; set; }
        public string NseSymbol { get; set; } = "";
        public string? CompanyName { get; set; }
        public string? Sector { get; set; }
        public int IsActive { get; set; }

        public Symbol ToDomain() => new(SymbolId, NseSymbol, CompanyName ?? NseSymbol, Sector ?? "", IsActive != 0);
    }

    private sealed class MappingRow
    {
        public int SymbolId { get; set; }
        public string Provider { get; set; } = "";
        public string ExternalId { get; set; } = "";
        public string EffectiveFrom { get; set; } = "";
        public string? EffectiveTo { get; set; }

        public SymbolMapping ToDomain() => new(
            SymbolId,
            Enum.Parse<DataProviderKind>(Provider),
            ExternalId,
            DateOnly.Parse(EffectiveFrom, CultureInfo.InvariantCulture),
            EffectiveTo is null ? null : DateOnly.Parse(EffectiveTo, CultureInfo.InvariantCulture));
    }
}
