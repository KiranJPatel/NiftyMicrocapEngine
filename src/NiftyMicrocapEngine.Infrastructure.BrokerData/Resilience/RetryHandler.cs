using Microsoft.Extensions.Logging;

namespace NiftyMicrocapEngine.Infrastructure.BrokerData.Resilience;

/// <summary>
/// Same retry/backoff handler as NiftyMicrocapEngine.Infrastructure.YahooFinance's
/// RetryHandler — duplicated here rather than shared, since neither
/// Infrastructure project references the other (each is independently
/// pluggable/swappable per the provider-abstraction design in §6.1), and
/// introducing a shared Infrastructure.Common project for one ~70-line class
/// would add more structural complexity than it saves. If a third
/// HTTP-calling infrastructure project is added later, that's the point to
/// factor this out for real.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private readonly int _maxRetries;
    private readonly ILogger _logger;
    private readonly Random _jitterSource = new();

    public RetryHandler(int maxRetries, ILogger logger)
    {
        _maxRetries = maxRetries;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        HttpResponseMessage? lastResponse = null;

        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var delay = ComputeBackoffDelay(attempt);
                _logger.LogWarning("Retrying {Method} {Uri} (attempt {Attempt}/{MaxRetries}) after {Delay}.",
                    request.Method, request.RequestUri, attempt, _maxRetries, delay);
                await Task.Delay(delay, cancellationToken);
            }

            lastResponse?.Dispose();
            lastResponse = null;

            try
            {
                var response = await base.SendAsync(request, cancellationToken);

                if (!IsTransientFailure(response))
                {
                    return response;
                }

                lastResponse = response;
                lastException = null;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }
        }

        if (lastResponse is not null) return lastResponse;
        throw lastException ?? new HttpRequestException("Request failed after all retry attempts, with no captured exception or response — unexpected retry-loop exit.");
    }

    private static bool IsTransientFailure(HttpResponseMessage response) =>
        (int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.RequestTimeout || (int)response.StatusCode == 429;

    private TimeSpan ComputeBackoffDelay(int attempt)
    {
        var baseDelayMs = Math.Pow(2, attempt) * 250;
        var jitterMs = _jitterSource.Next(0, 250);
        return TimeSpan.FromMilliseconds(baseDelayMs + jitterMs);
    }
}
