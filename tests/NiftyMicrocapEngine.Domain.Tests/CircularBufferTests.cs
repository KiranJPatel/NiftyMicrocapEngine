using NiftyMicrocapEngine.Domain;
using Xunit;

namespace NiftyMicrocapEngine.Domain.Tests;

public class CircularBufferTests
{
    [Fact]
    public void Add_BelowCapacity_CountReflectsItemsAdded()
    {
        var buffer = new CircularBuffer<int>(5);
        buffer.Add(1);
        buffer.Add(2);

        Assert.Equal(2, buffer.Count);
        Assert.False(buffer.IsFull);
    }

    [Fact]
    public void Add_AtCapacity_IsFullBecomesTrue()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.True(buffer.IsFull);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Add_BeyondCapacity_OverwritesOldest()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4); // overwrites 1

        Assert.Equal(3, buffer.Count);
        Assert.Equal(new[] { 2, 3, 4 }, buffer.ToArray());
    }

    [Fact]
    public void Indexer_ZeroIsNewest()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.Equal(3, buffer[0]);
        Assert.Equal(2, buffer[1]);
        Assert.Equal(1, buffer[2]);
    }

    [Fact]
    public void Indexer_AfterWraparound_StillCorrect()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4); // 1 overwritten
        buffer.Add(5); // 2 overwritten

        Assert.Equal(5, buffer[0]);
        Assert.Equal(4, buffer[1]);
        Assert.Equal(3, buffer[2]);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
    }

    [Fact]
    public void Enumeration_YieldsOldestToNewest()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4); // 1 overwritten

        Assert.Equal(new[] { 2, 3, 4 }, buffer.ToArray());
    }

    [Fact]
    public void Constructor_WithNonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircularBuffer<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircularBuffer<int>(-1));
    }
}
