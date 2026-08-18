using System.Collections;

namespace NiftyMicrocapEngine.Domain;

/// <summary>
/// Fixed-capacity ring buffer used for all rolling-window indicator state (moving
/// averages, ATR windows, etc). Matches build spec §3.1 exactly. Enumeration order
/// is oldest-to-newest; indexer is newest-relative (index 0 = most recently added).
/// </summary>
public sealed class CircularBuffer<T> : IEnumerable<T>
{
    private readonly T[] _items;
    private int _start;   // index of the oldest item
    private int _count;

    public CircularBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        _items = new T[capacity];
        _start = 0;
        _count = 0;
    }

    public int Capacity => _items.Length;
    public int Count => _count;
    public bool IsFull => _count == _items.Length;

    /// <summary>Adds an item. O(1). Overwrites the oldest item once the buffer is full.</summary>
    public void Add(T item)
    {
        var writeIndex = (_start + _count) % _items.Length;
        _items[writeIndex] = item;

        if (_count < _items.Length)
        {
            _count++;
        }
        else
        {
            // Full: overwrite oldest, advance start.
            _start = (_start + 1) % _items.Length;
        }
    }

    /// <summary>Indexes relative to the newest item: index 0 is most recently added.</summary>
    public T this[int indexFromNewest]
    {
        get
        {
            if (indexFromNewest < 0 || indexFromNewest >= _count)
                throw new ArgumentOutOfRangeException(nameof(indexFromNewest));

            // Newest item is at (_start + _count - 1) % capacity.
            var actualIndex = (_start + _count - 1 - indexFromNewest + _items.Length) % _items.Length;
            return _items[actualIndex];
        }
    }

    /// <summary>Yields items oldest-to-newest.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return _items[(_start + i) % _items.Length];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
