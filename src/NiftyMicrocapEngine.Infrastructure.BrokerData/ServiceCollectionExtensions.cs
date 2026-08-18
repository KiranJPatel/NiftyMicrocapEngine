using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Application.DataQuality;
using NiftyMicrocapEngine.Infrastructure.BrokerData.Nse;
using NiftyMicrocapEngine.Infrastructure.BrokerData.Resilience;

namespace NiftyMicrocapEngine.Infrastructure.BrokerData;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the broker (Zerodha) provider and the fallback router. Registers
    /// NotConfiguredBrokerCredentialProvider by default — replace with the real
    /// implementation before this is used against live data.
    /// </summary>
    public static IServiceCollection AddBrokerDataProvider(this IServiceCollection services)
    {
        services.AddSingleton<IBrokerCredentialProvider, NotConfiguredBrokerCredentialProvider>();

        services.AddHttpClient<IMarketDataProvider, ZerodhaMarketDataProvider>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<DataProvidersOptions>>().Value.Broker;
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .AddHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<DataProvidersOptions>>().Value.Broker;
                var logger = sp.GetRequiredService<ILogger<RetryHandler>>();
                return new RetryHandler(options.RetryCount, logger);
            });

        services.AddScoped<IMarketDataRouter, FallbackMarketDataRouter>();

        // §6.8's real circuit-band feed — see INseCircuitBandProvider's doc
        // comment for how the URL/format were verified. Registered as its
        // own HttpClient (not reusing IMarketDataProvider's, which is
        // shaped around candle-fetching, not this feed's single static
        // CSV) — no retry handler here, deliberately: this class already
        // does its own graceful-degradation (serve cached data, fall back
        // to empty) rather than blocking a scan on transient retries for a
        // feed that isn't request-critical the way live candle data is.
        services.AddHttpClient<INseCircuitBandProvider, NseCircuitBandProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
