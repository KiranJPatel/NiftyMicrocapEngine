using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftyMicrocapEngine.Application.Configuration;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Infrastructure.YahooFinance;

/// <summary>
/// Primary market data provider, backed by Yahoo Finance's unofficial chart endpoint.
/// Daily/Weekly only — see §6.2's note that intraday confirmation timeframes (H1/M30/
/// M15) must come from the broker provider instead. All parsing here is defensive:
/// a shape mismatch throws a clear exception rather than silently returning a wrong value.
/// SymbolId is not known to this provider — callers pass the provider-specific
/// symbol string (e.g. "RELIANCE.NS") and are responsible for mapping the resulting
/// Candle.SymbolId themselves (the router does this via SymbolMapping lookups).
/// This provider returns candles with SymbolId=0 as a sentinel; the router MUST
/// overwrite it before persisting.
/// </summary>
public sealed class YahooFinanceMarketDataProvider : IMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly DataProvidersOptions _options;
    private readonly ILogger<YahooFinanceMarketDataProvider> _logger;

    public DataProviderKind ProviderKind => DataProviderKind.Yahoo;

    public YahooFinanceMarketDataProvider(HttpClient httpClient, IOptions<DataProvidersOptions> options, ILogger<YahooFinanceMarketDataProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string providerSymbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (timeframe is not (Timeframe.Daily or Timeframe.Weekly))
            throw new NotSupportedException(
                $"YahooFinanceMarketDataProvider only supports Daily/Weekly. Requested: {timeframe}. " +
                "H1/M30/M15 confirmation data must come from the broker provider — see §6.2.");

        var interval = timeframe == Timeframe.Weekly ? "1wk" : "1d";
        var period1 = from.ToUnixTimeSeconds();
        var period2 = to.ToUnixTimeSeconds();

        var url = $"{_options.Yahoo.BaseUrl}/v8/finance/chart/{providerSymbol}" +
                   $"?period1={period1}&period2={period2}&interval={interval}&events=history";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Yahoo Finance request failed for {providerSymbol} ({timeframe}): {(int)response.StatusCode} {response.StatusCode}. " +
                $"Body (truncated): {Truncate(body, 500)}");
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        YahooChartResponse? parsed;
        try
        {
            parsed = await JsonSerializer.DeserializeAsync<YahooChartResponse>(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize Yahoo Finance response for {providerSymbol}. The endpoint shape may have " +
                "changed — re-run tools/DataAccessSmokeTest and update YahooChartResponse DTOs. See §24 Phase 0.", ex);
        }

        if (parsed?.Chart?.Error is { } error)
        {
            throw new InvalidOperationException($"Yahoo Finance returned an error for {providerSymbol}: {error.Code} - {error.Description}");
        }

        var result = parsed?.Chart?.Result?.FirstOrDefault();
        if (result is null) return Array.Empty<Candle>();

        var timestamps = result.Timestamp ?? new List<long>();
        var quote = result.Indicators?.Quote?.FirstOrDefault();
        var adjClose = result.Indicators?.AdjClose?.FirstOrDefault();

        if (quote is null || timestamps.Count == 0) return Array.Empty<Candle>();

        var candles = new List<Candle>();

        for (var i = 0; i < timestamps.Count; i++)
        {
            var open = quote.Open?.ElementAtOrDefault(i);
            var high = quote.High?.ElementAtOrDefault(i);
            var low = quote.Low?.ElementAtOrDefault(i);
            var close = quote.Close?.ElementAtOrDefault(i);
            var volume = quote.Volume?.ElementAtOrDefault(i);
            // AdjClose falls back to raw Close if the adjclose series is absent —
            // some Yahoo responses omit it entirely for certain symbols/ranges.
            var adjCloseValue = adjClose?.Values?.ElementAtOrDefault(i) ?? close;

            if (open is null || high is null || low is null || close is null || volume is null)
            {
                // Yahoo represents non-trading/incomplete bars as nulls within an
                // otherwise populated array. Skip rather than fabricate — the caller
                // (router) is responsible for recording a DataQualityFlag if the
                // resulting gap looks material.
                continue;
            }

            var timestamp = DateTimeOffset.FromUnixTimeSeconds(timestamps[i]);

            try
            {
                var candle = new Candle(
                    SymbolId: 0, // sentinel — router must overwrite before persisting
                    Timeframe: timeframe,
                    Timestamp: timestamp,
                    Open: (decimal)open.Value,
                    High: (decimal)high.Value,
                    Low: (decimal)low.Value,
                    Close: (decimal)close.Value,
                    AdjClose: (decimal)(adjCloseValue ?? close.Value),
                    Volume: volume.Value);

                candle.Validate();
                candles.Add(candle);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("Skipping invalid candle from Yahoo for {Symbol} at {Timestamp}: {Message}", providerSymbol, timestamp, ex.Message);
            }
        }

        return candles;
    }

    public async Task<ProviderHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var to = DateTimeOffset.UtcNow;
            var from = to.AddDays(-10);
            var candles = await GetCandlesAsync("RELIANCE.NS", Timeframe.Daily, from, to, ct);
            sw.Stop();

            return candles.Count == 0
                ? new ProviderHealthCheckResult(false, "Health check symbol returned no candles.", sw.Elapsed)
                : new ProviderHealthCheckResult(true, $"OK — {candles.Count} candles returned.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProviderHealthCheckResult(false, ex.Message, sw.Elapsed);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...(truncated)";
}
