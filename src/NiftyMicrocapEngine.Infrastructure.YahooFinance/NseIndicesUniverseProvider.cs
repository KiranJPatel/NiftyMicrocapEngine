using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;

namespace NiftyMicrocapEngine.Infrastructure.YahooFinance;

/// <summary>
/// Fetches the Nifty Microcap 250 constituent list from NSE Indices (§6.5).
///
/// IMPORTANT — access note (audit fix): nseindia.com is known to reject plain HTTP
/// GETs (User-Agent sniffing, session-cookie requirements, rate limiting). This
/// implementation uses session-replay: GET the public landing page first to acquire
/// cookies via the HttpClient's CookieContainer, THEN request the data endpoint
/// reusing that session. If NSE tightens this further, the documented fallback is a
/// headless-browser fetch — see §24 Phase 0, which must verify this approach works
/// against the live site before this provider is trusted.
/// </summary>
public sealed class NseIndicesUniverseProvider : IUniverseProvider
{
    // Verify this path against the live site during Phase 0 — NSE has historically
    // changed its constituent-list file naming/location without notice.
    private const string ConstituentListPath = "/content/indices/ind_niftymicrocap250list.csv";
    private const string LandingPagePath = "/";

    private readonly HttpClient _httpClient;
    private readonly NseIndicesOptions _options;
    private readonly ILogger<NseIndicesUniverseProvider> _logger;

    public NseIndicesUniverseProvider(HttpClient httpClient, IOptions<NseIndicesOptions> options, ILogger<NseIndicesUniverseProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(DateOnly EffectiveDate, IReadOnlyList<(string NseSymbol, string CompanyName, string? Sector)> Constituents)> GetCurrentUniverseAsync(CancellationToken ct = default)
    {
        await EstablishSessionAsync(ct);

        var url = $"{_options.BaseUrl}{ConstituentListPath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyBrowserLikeHeaders(request);

        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "NSE Indices returned 403 Forbidden even after session-replay. A headless-browser fetch may be " +
                "required instead — see §24 Phase 0 / §6.5.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"NSE Indices constituent list request failed: {(int)response.StatusCode} {response.StatusCode}.");
        }

        var csv = await response.Content.ReadAsStringAsync(ct);
        var constituents = ParseConstituentCsv(csv);

        if (constituents.Count == 0)
        {
            throw new InvalidOperationException(
                "NSE Indices constituent list parsed to zero rows — the file format has likely changed. " +
                "Verify the CSV schema against the live download and update ParseConstituentCsv. See §6.5.");
        }

        return (DateOnly.FromDateTime(DateTime.UtcNow), constituents);
    }

    public async Task<ProviderHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (_, constituents) = await GetCurrentUniverseAsync(ct);
            sw.Stop();
            return new ProviderHealthCheckResult(constituents.Count > 0, $"OK — {constituents.Count} constituents.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProviderHealthCheckResult(false, ex.Message, sw.Elapsed);
        }
    }

    private async Task EstablishSessionAsync(CancellationToken ct)
    {
        var landingUrl = $"{_options.BaseUrl}{LandingPagePath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, landingUrl);
        ApplyBrowserLikeHeaders(request);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("NSE Indices landing-page session priming returned {StatusCode}; proceeding anyway. See §6.5.", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NSE Indices landing-page session priming threw; proceeding anyway.");
        }
    }

    private static void ApplyBrowserLikeHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        request.Headers.Referrer = new Uri("https://www.nseindia.com/");
    }

    /// <summary>
    /// Parses the constituent CSV by header name (not fixed column index) so minor
    /// reordering doesn't silently misassign fields. Exact column layout NOT
    /// guaranteed — must be verified against the live file at Phase 0.
    /// </summary>
    private static List<(string, string, string?)> ParseConstituentCsv(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return new List<(string, string, string?)>();

        var headers = lines[0].Split(',').Select(h => h.Trim().Trim('"')).ToList();
        int IndexOf(string name) => headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

        var symbolIdx = IndexOf("Symbol");
        var nameIdx = IndexOf("Company Name");
        var industryIdx = IndexOf("Industry");

        if (symbolIdx < 0)
        {
            throw new InvalidOperationException(
                $"NSE constituent CSV is missing the expected 'Symbol' column. Actual headers: [{string.Join(", ", headers)}]. " +
                "Update ParseConstituentCsv per §6.5.");
        }

        var result = new List<(string, string, string?)>();

        for (var i = 1; i < lines.Length; i++)
        {
            var fields = lines[i].Split(',').Select(f => f.Trim().Trim('"')).ToArray();
            if (fields.Length <= symbolIdx) continue;

            var symbol = fields[symbolIdx];
            var name = nameIdx >= 0 && fields.Length > nameIdx ? fields[nameIdx] : symbol;
            var sector = industryIdx >= 0 && fields.Length > industryIdx ? fields[industryIdx] : null;

            result.Add((symbol, name, sector));
        }

        return result;
    }
}
