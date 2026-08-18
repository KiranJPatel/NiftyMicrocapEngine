using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Domain.Tests;

public class CandleTests
{
    private static Candle ValidCandle() => new(
        SymbolId: 1,
        Timeframe: Timeframe.Daily,
        Timestamp: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
        Open: 100m, High: 105m, Low: 99m, Close: 102m, AdjClose: 102m, Volume: 10000);

    [Fact]
    public void Validate_WithValidCandle_DoesNotThrow()
    {
        var exception = Record.Exception(() => ValidCandle().Validate());
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WhenHighLessThanLow_Throws()
    {
        var candle = ValidCandle() with { High = 90m, Low = 99m };
        Assert.Throws<InvalidOperationException>(() => candle.Validate());
    }

    [Fact]
    public void Validate_WhenCloseOutsideRange_Throws()
    {
        var candle = ValidCandle() with { Close = 200m };
        Assert.Throws<InvalidOperationException>(() => candle.Validate());
    }

    [Fact]
    public void Validate_WhenVolumeNegative_Throws()
    {
        var candle = ValidCandle() with { Volume = -1 };
        Assert.Throws<InvalidOperationException>(() => candle.Validate());
    }

    [Fact]
    public void Validate_WhenAdjCloseNotPositive_Throws()
    {
        var candle = ValidCandle() with { AdjClose = 0m };
        Assert.Throws<InvalidOperationException>(() => candle.Validate());
    }
}
