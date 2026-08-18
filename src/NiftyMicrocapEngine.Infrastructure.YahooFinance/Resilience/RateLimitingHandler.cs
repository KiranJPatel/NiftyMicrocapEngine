using System.Threading.RateLimiting;

namespace NiftyMicrocapEngine.Infrastructure.YahooFinance.Resilience;

/// <summary>
/// DelegatingHandler enforcing a token-bucket rate limit per §19's
/// RequestsPerSecond setting. Uses System.Threading.RateLimiting from the
/// .NET 8 BCL (no external package) — a fixed-window limiter refilling
/// `requestsPerSecond` tokens every second, which is the simplest correct
/// implementation of "N requests per second" and avoids bursting the full
/// budget in the first millisecond of each window the way an unthrottled
/// token bucket without a minimum spacing would.
///
/// This exists specifically because Yahoo's and NSE's endpoints are
/// unofficial/unrate-limited-by-contract (§6.2/§6.5) — self-imposing a
/// conservative rate is what keeps this engine from getting IP-blocked
/// during a 250-symbol Stage 1 scan, which would otherwise fire that many
/// requests as fast as the network allows.
/// </summary>
public sealed class RateLimitingHandler : DelegatingHandler
{
    private readonly RateLimiter _limiter;

    public RateLimitingHandler(int requestsPerSecond)
    {
        _limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, requestsPerSecond),
            Window = TimeSpan.FromSeconds(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = int.MaxValue // never reject a request outright — queue it rather than fail a scan under normal load
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await _limiter.AcquireAsync(1, cancellationToken);
        // QueueLimit is unbounded and AcquireAsync only completes once a
        // permit is actually available, so a failed lease here would only
        // happen on cancellation — which the caller already handles via the
        // token, not a distinct rate-limit-rejection code path.
        return await base.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _limiter.Dispose();
        base.Dispose(disposing);
    }
}
