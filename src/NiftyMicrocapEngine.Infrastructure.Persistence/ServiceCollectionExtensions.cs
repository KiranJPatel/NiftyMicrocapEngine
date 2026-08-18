using Microsoft.Extensions.DependencyInjection;
using NiftyMicrocapEngine.Application.Persistence;

namespace NiftyMicrocapEngine.Infrastructure.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlitePersistence(this IServiceCollection services, string sqliteFilePath)
    {
        var connectionString = $"Data Source={sqliteFilePath}";

        services.AddSingleton<ISqliteConnectionFactory>(new SqliteConnectionFactory(connectionString));
        services.AddSingleton(sp => new MigrationRunner(connectionString, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MigrationRunner>>()));

        services.AddScoped<ISymbolRepository, SqliteSymbolRepository>();
        services.AddScoped<IUniverseRepository, SqliteUniverseRepository>();
        services.AddScoped<ICandleRepository, SqliteCandleRepository>();
        services.AddScoped<IDataQualityFlagRepository, SqliteDataQualityFlagRepository>();
        services.AddScoped<IIndicatorValueRepository, SqliteIndicatorValueRepository>();
        services.AddScoped<IMarketStructureEventRepository, SqliteMarketStructureEventRepository>();
        services.AddScoped<IAnalysisRepository, SqliteAnalysisRepository>();
        services.AddScoped<IScanHistoryRepository, SqliteScanHistoryRepository>();

        return services;
    }
}
