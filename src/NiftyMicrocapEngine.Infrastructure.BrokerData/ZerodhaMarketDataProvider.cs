using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Infrastructure.BrokerData;

/// <summary>
/// Secondary market data provider backed by Zerodha's Kite Connect historical-data
/// API. Used as: (a) fallback when Yahoo is unavailable/gapped, and (b) the sole
/// source for H1/M30/M15 confirmation-only candles.
///
/// Instrument-token resolution (Kite requires a numeric instrument_token, not the
/// trading symbol) is left as a TODO: reuse whatever instrument-master lookup
/// NiftySMC/NiftyOptionsSMC already implement, rather than re-fetching Kite's full
/// instrument dump here.
/// </summary>
public sealed class ZerodhaMarketDataProvider : IMarketDataProvider
{
    private const string KiteBaseUrl = "https://api.kite.trade";

    private readonly HttpClient _httpClient;
    private readonly IBrokerCredentialProvider _credentialProvider;
    private readonly ILogger<ZerodhaMarketDataProvider> _logger;

    public DataProviderKind ProviderKind => DataProviderKind.Broker;

    public ZerodhaMarketDataProvider(HttpClient httpClient, IBrokerCredentialProvider credentialProvider, ILogger<ZerodhaMarketDataProvider> logger)
    {
        if (credentialProvider.Kind != BrokerKind.Zerodha)
            throw new ArgumentException($"ZerodhaMarketDataProvider requires Kind == Zerodha, got {credentialProvider.Kind}.", nameof(credentialProvider));

        _httpClient = httpClient;
        _credentialProvider = credentialProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string providerSymbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var instrumentToken = await ResolveInstrumentTokenAsync(providerSymbol, ct);
        var interval = ToKiteInterval(timeframe);
        var accessToken = await _credentialProvider.GetAccessTokenAsync(ct);

        var url = $"{KiteBaseUrl}/instruments/historical/{instrumentToken}/{interval}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Kite-Version", "3");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", accessToken);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Zerodha historical-data request failed for {providerSymbol} ({timeframe}): {(int)response.StatusCode} {response.StatusCode}. " +
                $"Body (truncated): {(body.Length > 300 ? body[..300] + "..." : body)}");
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var parsed = await JsonSerializer.DeserializeAsync<KiteHistoricalResponse>(stream, cancellationToken: ct);

        if (parsed?.Data?.Candles is not { } rawCandles) return Array.Empty<Candle>();

        var candles = new List<Candle>();

        foreach (var candle in rawCandles)
        {
            if (candle.Count < 6)
            {
                _logger.LogWarning("Skipping malformed Zerodha candle for {Symbol}: expected 6 elements, got {Count}.", providerSymbol, candle.Count);
                continue;
            }

            try
            {
                var timestamp = DateTimeOffset.Parse(candle[0].GetString()!);
                var close = candle[4].GetDecimal();

                var domainCandle = new Candle(
                    SymbolId: 0, // sentinel — router must overwrite before persisting
                    Timeframe: timeframe,
                    Timestamp: timestamp,
                    Open: candle[1].GetDecimal(),
                    High: candle[2].GetDecimal(),
                    Low: candle[3].GetDecimal(),
                    Close: close,
                    AdjClose: close, // Kite's historical API doesn't distinguish adjusted close
                    Volume: candle[5].GetInt64());

                domainCandle.Validate();
                candles.Add(domainCandle);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                _logger.LogWarning(ex, "Skipping invalid Zerodha candle for {Symbol}.", providerSymbol);
            }
        }

        return candles;
    }

    public async Task<ProviderHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _credentialProvider.GetAccessTokenAsync(ct);
            sw.Stop();
            return new ProviderHealthCheckResult(true, "Credential retrieval OK (full data check requires a resolvable instrument token).", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProviderHealthCheckResult(false, ex.Message, sw.Elapsed);
        }
    }

    /// <summary>
    /// TODO (integration time): reuse the instrument-master lookup already
    /// implemented in NiftySMC/NiftyOptionsSMC rather than reimplementing it here.
    /// </summary>
    private Task<long> ResolveInstrumentTokenAsync(string providerSymbol, CancellationToken ct)
    {
        throw new NotImplementedException(
            $"Instrument token resolution for '{providerSymbol}' is not wired yet. Reuse the existing " +
            "NiftySMC/NiftyOptionsSMC instrument-master lookup at integration time.");
    }

    private static string ToKiteInterval(Timeframe timeframe) => timeframe switch
    {
        Timeframe.Weekly => "week",
        Timeframe.Daily => "day",
        Timeframe.H1 => "60minute",
        Timeframe.M30 => "30minute",
        Timeframe.M15 => "15minute",
        _ => throw new NotSupportedException($"Timeframe {timeframe} is out of scope for this engine.")
    };
}

internal sealed class KiteHistoricalResponse
{
    [JsonPropertyName("data")]
    public KiteHistoricalData? Data { get; set; }
}

internal sealed class KiteHistoricalData
{
    [JsonPropertyName("candles")]
    public List<List<JsonElement>>? Candles { get; set; }
}
