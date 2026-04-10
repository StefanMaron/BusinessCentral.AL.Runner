namespace AlRunner.Runtime;

using System.Collections;

/// <summary>
/// Generic replacement for NavArray&lt;T&gt; that doesn't require ITreeObject.
/// Provides 0-based indexing matching the C# layer of NavArray (the AL compiler
/// translates 1-based AL indices to 0-based C# indices in the transpiled code).
/// </summary>
public class MockArray<T> : IEnumerable<T>
{
    private readonly T[] _items;
    private readonly Func<T>? _factory;

    /// <summary>
    /// Constructor matching NavArray(T initValue, params int[] dimensions).
    /// Used after rewriter drops ITreeObject: new MockArray&lt;T&gt;(defaultValue, size).
    /// </summary>
    public MockArray(T defaultValue, params int[] dimensions)
    {
        int totalLength = 1;
        foreach (var d in dimensions) totalLength *= d;
        _factory = null;
        _items = new T[totalLength];
        for (int i = 0; i < totalLength; i++)
            _items[i] = defaultValue;
    }

    /// <summary>
    /// Factory constructor: creates array with factory-produced elements.
    /// Used for MockVariant arrays: new MockArray&lt;MockVariant&gt;(size, () => new MockVariant())
    /// </summary>
    public MockArray(int length, Func<T> factory)
    {
        _factory = factory;
        _items = new T[length];
        for (int i = 0; i < length; i++)
            _items[i] = factory();
    }

    /// <summary>0-based indexer matching NavArray C# semantics.</summary>
    public T this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>Multi-dimensional indexer (flattened).</summary>
    public T this[int[] indexes]
    {
        get => _items[indexes[0]];
        set => _items[indexes[0]] = value;
    }

    public int Length => _items.Length;
    public int ArrayLen() => _items.Length;
    public int ArrayLen(int dimension) => _items.Length;

    public void Clear()
    {
        for (int i = 0; i < _items.Length; i++)
            _items[i] = _factory != null ? _factory() : default!;
    }

    public void Clear(int[] indexes)
    {
        _items[indexes[0]] = _factory != null ? _factory() : default!;
    }

    public void ClearReference() => Clear();
    public void ClearReference(int[] indexes) => Clear(indexes);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
