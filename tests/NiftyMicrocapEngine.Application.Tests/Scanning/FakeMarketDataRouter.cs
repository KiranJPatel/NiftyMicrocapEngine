using NiftyMicrocapEngine.Application.DataAccess;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Tests.Scanning;

public sealed class FakeMarketDataRouter : IMarketDataRouter
{
    private readonly Dictionary<int, string> _trendBySymbol;

    public FakeMarketDataRouter(Dictionary<int, string> trendBySymbol)
    {
        _trendBySymbol = trendBySymbol;
    }

    public Task<MarketDataFetchResult> GetCandlesAsync(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (timeframe.IsConfirmationOnly())
        {
            return Task.FromResult(new MarketDataFetchResult(Array.Empty<Candle>(), Array.Empty<DataQualityFlag>()));
        }

        var trend = _trendBySymbol.GetValueOrDefault(symbolId, "flat");
        var candles = GenerateSeries(symbolId, timeframe, from, to, trend);
        return Task.FromResult(new MarketDataFetchResult(candles, Array.Empty<DataQualityFlag>()));
    }

    private static List<Candle> GenerateSeries(int symbolId, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, string trend)
    {
        var candles = new List<Candle>();
        var rnd = new Random(symbolId);
        var price = 100m;
        var stepDays = timeframe == Timeframe.Weekly ? 7 : 1;

        var current = from;
        while (current <= to)
        {
            var drift = TrendDrift(trend);
            var noise = (decimal)(rnd.NextDouble() * 1.0 - 0.5);
            var close = Math.Max(1m, price + drift + noise);
            var high = Math.Max(price, close) + 0.3m;
            var low = Math.Min(price, close) - 0.3m;

            candles.Add(new Candle(symbolId, timeframe, current, price, high, low, close, close, 10000 + rnd.Next(-2000, 2000)));

            price = close;
            current = current.AddDays(stepDays);
        }

        return candles;
    }

    private static decimal TrendDrift(string trend)
    {
        if (trend == "up") return 0.5m;
        if (trend == "down") return -0.5m;
        return 0m;
    }
}
