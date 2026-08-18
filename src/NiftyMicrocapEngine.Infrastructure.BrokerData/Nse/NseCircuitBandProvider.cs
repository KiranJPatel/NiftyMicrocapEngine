using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using NiftyMicrocapEngine.Application.DataQuality;

namespace NiftyMicrocapEngine.Infrastructure.BrokerData.Nse;

/// <summary>
/// See INseCircuitBandProvider's doc comment for how the feed URL and
/// format were verified. Registered via AddHttpClient (see
/// ServiceCollectionExtensions), so retry/timeout policy matches the same
/// convention as ZerodhaMarketDataProvider's HttpClient registration.
/// </summary>
public sealed class NseCircuitBandProvider : INseCircuitBandProvider
{
    private const string PriceBandCsvUrl = "https://nsearchives.nseindia.com/content/equities/sec_list.csv";

    /// <summary>
    /// NSE republishes this file roughly daily; a symbol's band can also
    /// change mid-session via a surveillance action, but re-fetching more
    /// aggressively than this buys little accuracy for a lot more load on a
    /// public archive endpoint that isn't rate-limit-documented.
    /// </summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    private readonly HttpClient _httpClient;
    private readonly ILogger<NseCircuitBandProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyDictionary<string, decimal>? _cachedBands;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public NseCircuitBandProvider(HttpClient httpClient, ILogger<NseCircuitBandProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetCircuitBandsAsync(CancellationToken ct = default)
    {
        if (_cachedBands is not null && DateTimeOffset.UtcNow - _cachedAt < CacheDuration)
        {
            return _cachedBands;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock — a concurrent caller may
            // have already refreshed while this one was waiting.
            if (_cachedBands is not null && DateTimeOffset.UtcNow - _cachedAt < CacheDuration)
            {
                return _cachedBands;
            }

            var csv = await _httpClient.GetStringAsync(PriceBandCsvUrl, ct);
            var parsed = Parse(csv);

            if (parsed.Count == 0)
            {
                _logger.LogWarning(
                    "NSE circuit-band feed returned zero parseable rows — keeping the previous cached snapshot ({PreviousCount} symbols) rather than replacing it with an empty one.",
                    _cachedBands?.Count ?? 0);
                return _cachedBands ?? parsed;
            }

            _cachedBands = parsed;
            _cachedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Refreshed NSE circuit-band feed: {Count} symbols.", parsed.Count);
            return _cachedBands;
        }
        catch (Exception ex)
        {
            // A feed failure must degrade gracefully, not take down a scan.
            // ICircuitBandTracker's band-aware overload already treats a
            // missing band for a symbol as "fall back to the zero-range
            // heuristic" — returning the last good snapshot (or empty, on a
            // cold start with no prior success) preserves that fallback path.
            _logger.LogWarning(ex,
                "Failed to fetch or parse the NSE circuit-band feed — {Fallback}.",
                _cachedBands is not null ? "serving the previous cached snapshot" : "no cached snapshot available yet, returning empty (callers fall back to the zero-range heuristic)");
            return _cachedBands ?? new Dictionary<string, decimal>();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Parses NSE's "Symbol,Series,Security Name,Band,Remarks" CSV. A
    /// symbol can appear once per Series it's listed under (observed
    /// directly: e.g. AAREYDRUGS appears as both BE-series at one band and
    /// EQ-series at another) — the EQ-series row is preferred when present,
    /// since EQ is NSE's standard listed series and what this system's
    /// OHLCV data (via Yahoo) actually reflects; BE/BZ/SM/ST are
    /// alternate/restricted series whose band isn't representative of the
    /// symbol's normal trading. Falls back to whichever row appears first
    /// when no EQ row exists for that symbol.
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> Parse(string csv)
    {
        var bestBySymbol = new Dictionary<string, (decimal Band, bool IsEq)>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(csv);
        reader.ReadLine(); // header row: "Symbol,Series,Security Name,Band,Remarks" — skip

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = SplitCsvLine(line);
            if (fields.Count < 4) continue;

            var symbol = fields[0].Trim();
            var series = fields[1].Trim();
            if (symbol.Length == 0) continue;
            if (!decimal.TryParse(fields[3].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var bandPercent))
            {
                continue; // a malformed row shouldn't take down the whole parse
            }

            var bandFraction = bandPercent / 100m;
            var isEq = string.Equals(series, "EQ", StringComparison.OrdinalIgnoreCase);

            if (!bestBySymbol.TryGetValue(symbol, out var existing))
            {
                bestBySymbol[symbol] = (bandFraction, isEq);
            }
            else if (isEq && !existing.IsEq)
            {
                bestBySymbol[symbol] = (bandFraction, true);
            }
        }

        return bestBySymbol.ToDictionary(kv => kv.Key, kv => kv.Value.Band, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Minimal CSV splitter handling the one quoting case this file actually
    /// uses (the Remarks column, e.g. "GSM STAGE - 0") — not a general CSV
    /// parser, since Symbol/Series/Band never contain commas or quotes in
    /// NSE's own data.
    /// </summary>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
