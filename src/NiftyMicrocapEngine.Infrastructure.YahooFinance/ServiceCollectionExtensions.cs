using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Infrastructure.YahooFinance.Resilience;

namespace NiftyMicrocapEngine.Infrastructure.YahooFinance;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddYahooFinanceProvider(this IServiceCollection services)
    {
        // Registered as factories (not DelegatingHandler singletons shared
        // across clients) because AddHttpMessageHandler resolves a new
        // handler instance per HttpClient build per the framework's own
        // handler lifetime rules — sharing a RateLimiter instance across the
        // Yahoo and NSE clients would incorrectly apply one combined budget
        // to what are actually two independently-configured endpoints.
        services.AddHttpClient<IMarketDataProvider, YahooFinanceMarketDataProvider>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<DataProvidersOptions>>().Value.Yahoo;
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .AddHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<DataProvidersOptions>>().Value.Yahoo;
                var logger = sp.GetRequiredService<ILogger<RetryHandler>>();
                return new RetryHandler(options.RetryCount, logger);
            })
            .AddHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<DataProvidersOptions>>().Value.Yahoo;
                return new RateLimitingHandler(options.RequestsPerSecond);
            });

        services.AddHttpClient<IUniverseProvider, NseIndicesUniverseProvider>((sp, client) =>
            {
                // §19 has no dedicated NSE timeout setting — reuse the Broker
                // provider's TimeoutSeconds as the closest configured analog
                // (both are "secondary, non-Yahoo NSE-adjacent" endpoints)
                // rather than hardcode a new magic number.
                var options = sp.GetRequiredService<IOptions<DataProvidersOptions>>().Value.Broker;
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                CookieContainer = new System.Net.CookieContainer(),
                UseCookies = true
            })
            .AddHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<DataProvidersOptions>>().Value.Broker;
                var logger = sp.GetRequiredService<ILogger<RetryHandler>>();
                return new RetryHandler(options.RetryCount, logger);
            });

        return services;
    }
}
