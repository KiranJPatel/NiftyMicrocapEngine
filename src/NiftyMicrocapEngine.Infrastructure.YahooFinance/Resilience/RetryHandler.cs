using Microsoft.Extensions.Logging;

namespace NiftyMicrocapEngine.Infrastructure.YahooFinance.Resilience;

/// <summary>
/// DelegatingHandler implementing exponential backoff with jitter for
/// transient HTTP failures (5xx, 408, and network-level exceptions). Built
/// on the BCL alone (no Polly dependency) so it doesn't add an external
/// package this sandbox can't verify resolves cleanly — a production
/// deployment with real NuGet access may prefer swapping this for
/// Microsoft.Extensions.Http.Resilience, which offers more configurable
/// policies (circuit breakers, jittered backoff strategies) for the same
/// intent; this handler covers the same failure modes with plain BCL retry.
///
/// Does NOT retry 4xx responses other than 408/429 (retrying a genuine
/// client error like 404 wastes calls and delays surfacing a real bug) and
/// does NOT retry non-idempotent requests — every call this engine makes to
/// Yahoo/NSE is a GET, so idempotency is a given here, not something this
/// handler checks per-request.
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
                // A client-side timeout (not caller cancellation) manifests as
                // TaskCanceledException in HttpClient — treat as transient.
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
        var baseDelayMs = Math.Pow(2, attempt) * 250; // 500ms, 1s, 2s, 4s...
        var jitterMs = _jitterSource.Next(0, 250);
        return TimeSpan.FromMilliseconds(baseDelayMs + jitterMs);
    }
}
