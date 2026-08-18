using System.Text.Json.Serialization;

namespace NiftyMicrocapEngine.Infrastructure.YahooFinance;

// ============================================================================
// Reflects Yahoo's chart endpoint response shape as of this engine's last
// verification. This endpoint is UNOFFICIAL and UNDOCUMENTED — see §24 Phase 0
// in the build spec. The Phase 0 smoke test (tools/DataAccessSmokeTest) must be
// run against the LIVE endpoint before relying on this shape.
// ============================================================================

public sealed class YahooChartResponse
{
    [JsonPropertyName("chart")]
    public YahooChart? Chart { get; set; }
}

public sealed class YahooChart
{
    [JsonPropertyName("result")]
    public List<YahooChartResult>? Result { get; set; }

    [JsonPropertyName("error")]
    public YahooChartError? Error { get; set; }
}

public sealed class YahooChartError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class YahooChartResult
{
    [JsonPropertyName("meta")]
    public YahooChartMeta? Meta { get; set; }

    [JsonPropertyName("timestamp")]
    public List<long>? Timestamp { get; set; }

    [JsonPropertyName("indicators")]
    public YahooIndicators? Indicators { get; set; }
}

public sealed class YahooChartMeta
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

public sealed class YahooIndicators
{
    [JsonPropertyName("quote")]
    public List<YahooQuote>? Quote { get; set; }

    [JsonPropertyName("adjclose")]
    public List<YahooAdjClose>? AdjClose { get; set; }
}

public sealed class YahooQuote
{
    [JsonPropertyName("open")]
    public List<double?>? Open { get; set; }

    [JsonPropertyName("high")]
    public List<double?>? High { get; set; }

    [JsonPropertyName("low")]
    public List<double?>? Low { get; set; }

    [JsonPropertyName("close")]
    public List<double?>? Close { get; set; }

    [JsonPropertyName("volume")]
    public List<long?>? Volume { get; set; }
}

public sealed class YahooAdjClose
{
    [JsonPropertyName("adjclose")]
    public List<double?>? Values { get; set; }
}
