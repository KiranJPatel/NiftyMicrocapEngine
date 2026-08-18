// ============================================================================
// Phase 0 — Data Access Smoke Test. Run this BEFORE trusting any other code in
// this solution that talks to Yahoo or NSE. Standalone: no project references,
// so it can be run and debugged without building the whole solution.
//
// Usage: cd tools/DataAccessSmokeTest && dotnet run
// Exit code 0 = all checks passed.
// ============================================================================

using System.Net;
using System.Text.Json;

var results = new List<(string CheckName, bool Passed, string Detail)>();

Console.WriteLine("=== Nifty Microcap Engine — Phase 0 Data Access Smoke Test ===");
Console.WriteLine($"Run started (UTC): {DateTimeOffset.UtcNow:O}");
Console.WriteLine();

var yahooSymbols = new[] { "RELIANCE.NS", "TCS.NS", "INFY.NS" };
using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
{
    foreach (var symbol in yahooSymbols)
    {
        var (passed, detail) = await CheckYahooDailyAsync(httpClient, symbol);
        results.Add(($"Yahoo Daily — {symbol}", passed, detail));
    }

    var (weeklyPassed, weeklyDetail) = await CheckYahooWeeklyAsync(httpClient, "RELIANCE.NS");
    results.Add(("Yahoo Weekly — RELIANCE.NS", weeklyPassed, weeklyDetail));
}

var (nsePassed, nseDetail) = await CheckNseConnectivityAsync();
results.Add(("NSE Indices — session-replay connectivity", nsePassed, nseDetail));

Console.WriteLine();
Console.WriteLine("=== Results ===");
foreach (var (checkName, passed, detail) in results)
{
    Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {checkName}");
    Console.WriteLine($"       {detail}");
}

var allPassed = results.All(r => r.Passed);
Console.WriteLine();
Console.WriteLine(allPassed
    ? "All checks passed. Safe to proceed."
    : "One or more checks FAILED. Resolve before trusting the rest of the solution — see §24 Phase 0 / §6.2 / §6.5.");

return allPassed ? 0 : 1;

static async Task<(bool Passed, string Detail)> CheckYahooDailyAsync(HttpClient httpClient, string symbol)
{
    try
    {
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-30);
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}" +
                   $"?period1={from.ToUnixTimeSeconds()}&period2={to.ToUnixTimeSeconds()}&interval=1d&events=history";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

        using var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            return (false, $"HTTP {(int)response.StatusCode} {response.StatusCode}. Body (truncated): {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("chart", out var chart))
            return (false, "Response JSON has no top-level 'chart' property. Shape has changed — update YahooChartResponse DTOs.");

        if (chart.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            return (false, $"Yahoo returned a chart-level error: {error}");

        if (!chart.TryGetProperty("result", out var resultArray) || resultArray.ValueKind != JsonValueKind.Array || resultArray.GetArrayLength() == 0)
            return (false, "'chart.result' is missing, not an array, or empty.");

        var result = resultArray[0];
        var hasTimestamp = result.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Array && ts.GetArrayLength() > 0;
        var hasQuote = result.TryGetProperty("indicators", out var indicators)
            && indicators.TryGetProperty("quote", out var quoteArr)
            && quoteArr.ValueKind == JsonValueKind.Array && quoteArr.GetArrayLength() > 0;

        if (!hasTimestamp) return (false, "'chart.result[0].timestamp' missing, not an array, or empty.");
        if (!hasQuote) return (false, "'chart.result[0].indicators.quote[0]' missing or empty.");

        var hasAdjClose = result.TryGetProperty("indicators", out var ind2) && ind2.TryGetProperty("adjclose", out _);

        return (true, $"OK — {ts.GetArrayLength()} daily bars for {symbol}. adjclose field present: {hasAdjClose}.");
    }
    catch (TaskCanceledException)
    {
        return (false, "Request timed out after 30s.");
    }
    catch (JsonException ex)
    {
        return (false, $"Response was not valid JSON: {ex.Message}");
    }
    catch (Exception ex)
    {
        return (false, $"Unexpected exception: {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task<(bool Passed, string Detail)> CheckYahooWeeklyAsync(HttpClient httpClient, string symbol)
{
    try
    {
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-180);
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}" +
                   $"?period1={from.ToUnixTimeSeconds()}&period2={to.ToUnixTimeSeconds()}&interval=1wk&events=history";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

        using var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            return (false, $"HTTP {(int)response.StatusCode} {response.StatusCode}. Body (truncated): {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
        var barCount = result.GetProperty("timestamp").GetArrayLength();

        return (true, $"OK — {barCount} weekly bars for {symbol}.");
    }
    catch (Exception ex)
    {
        return (false, $"Unexpected exception: {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task<(bool Passed, string Detail)> CheckNseConnectivityAsync()
{
    try
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        void ApplyHeaders(HttpRequestMessage req)
        {
            req.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        }

        using (var landingRequest = new HttpRequestMessage(HttpMethod.Get, "https://www.nseindia.com/"))
        {
            ApplyHeaders(landingRequest);
            using var landingResponse = await httpClient.SendAsync(landingRequest);
            if (!landingResponse.IsSuccessStatusCode)
            {
                return (false,
                    $"Landing page priming failed: HTTP {(int)landingResponse.StatusCode} {landingResponse.StatusCode}. " +
                    "NSE may be blocking this environment's IP/UA outright; a headless-browser approach may be required. See §6.5.");
            }
        }

        using var dataRequest = new HttpRequestMessage(HttpMethod.Get, "https://nsearchives.nseindia.com/content/indices/ind_niftymicrocap250list.csv");
        ApplyHeaders(dataRequest);
        dataRequest.Headers.Referrer = new Uri("https://www.nseindia.com/");

        using var dataResponse = await httpClient.SendAsync(dataRequest);
        var body = await dataResponse.Content.ReadAsStringAsync();

        if (dataResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            return (false,
                "Data endpoint returned 403 even after session-replay. Likely needs a headless-browser fetch instead. See §24 Phase 0 / §6.5.");
        }

        if (!dataResponse.IsSuccessStatusCode)
        {
            return (false, $"Data endpoint HTTP {(int)dataResponse.StatusCode} {dataResponse.StatusCode}. Body (truncated): {Truncate(body, 300)}");
        }

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return (false, "Response returned successfully but parsed to fewer than 2 lines. File may be empty or in an unexpected format.");

        var header = lines[0];
        var looksLikeExpectedCsv = header.Contains("Symbol", StringComparison.OrdinalIgnoreCase);

        return looksLikeExpectedCsv
            ? (true, $"OK — {lines.Length - 1} constituent rows found. Header: {Truncate(header, 200)}")
            : (false, $"Response parsed but header doesn't contain 'Symbol' as expected. Header (truncated): {Truncate(header, 200)}. Verify CSV schema — see §6.5.");
    }
    catch (TaskCanceledException)
    {
        return (false, "Request timed out after 30s.");
    }
    catch (Exception ex)
    {
        return (false, $"Unexpected exception: {ex.GetType().Name}: {ex.Message}");
    }
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...(truncated)";
