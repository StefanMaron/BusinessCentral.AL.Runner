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
    private readonly int[] _dimensions;

    /// <summary>
    /// Constructor matching NavArray(T initValue, params int[] dimensions).
    /// Used after rewriter drops ITreeObject: new MockArray&lt;T&gt;(defaultValue, size).
    /// </summary>
    public MockArray(T defaultValue, params int[] dimensions)
    {
        _dimensions = dimensions.Length > 0 ? (int[])dimensions.Clone() : new[] { 0 };
        int totalLength = 1;
        foreach (var d in _dimensions) totalLength *= d;
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
        _dimensions = new[] { length };
        _factory = factory;
        _items = new T[length];
        for (int i = 0; i < length; i++)
            _items[i] = factory();
    }

    /// <summary>0-based indexer matching NavArray C# semantics (1-D).</summary>
    public T this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>
    /// 2-D indexer: BC emits <c>arr[r, c]</c> for <c>Array[M, N]</c> access.
    /// Both indices are 0-based (BC translates 1-based AL indices before emit).
    /// Flat index = r * dim[1] + c.
    /// </summary>
    public T this[int r, int c]
    {
        get => _items[FlatIndex(r, c)];
        set => _items[FlatIndex(r, c)] = value;
    }

    /// <summary>
    /// 3-D indexer: BC emits <c>arr[x, y, z]</c> for <c>Array[M, N, P]</c> access.
    /// All indices are 0-based.
    /// Flat index = x * dim[1] * dim[2] + y * dim[2] + z.
    /// </summary>
    public T this[int x, int y, int z]
    {
        get => _items[FlatIndex(x, y, z)];
        set => _items[FlatIndex(x, y, z)] = value;
    }

    /// <summary>
    /// Multi-dimensional indexer via int-array (legacy path).
    /// BC may emit <c>arr[new int[]{r,c}]</c> for some code paths.
    /// Computes the flat index from all provided dimension indices.
    /// </summary>
    public T this[int[] indexes]
    {
        get => _items[FlatIndex(indexes)];
        set => _items[FlatIndex(indexes)] = value;
    }

    public int Length => _items.Length;

    /// <summary>
    /// AL ArrayLen(arr) — returns the length of the first dimension.
    /// For a 1-D array[N], returns N. For multi-D array[X,Y,...] returns X.
    /// </summary>
    public int ArrayLen() => _dimensions.Length > 0 ? _dimensions[0] : 0;

    /// <summary>
    /// AL ArrayLen(arr, dimension) — returns the length of the specified 1-based dimension.
    /// For array[3,4], dimension=1 returns 3, dimension=2 returns 4.
    /// </summary>
    public int ArrayLen(int dimension)
    {
        // BC compiler generates 0-based dimension indices in the transpiled C# code
        return (dimension >= 0 && dimension < _dimensions.Length) ? _dimensions[dimension] : 0;
    }

    /// <summary>
    /// BC emits <c>arr.GetSubArray(rowIndex)</c> when a row of a 2-D array is passed
    /// to a parameter that expects a 1-D array (e.g. <c>Func(arr[1])</c> where Func
    /// takes <c>Array[N]</c>).  The <paramref name="rowIndex"/> is 0-based.
    /// Returns a new <c>MockArray&lt;T&gt;</c> containing the elements of the specified row.
    /// </summary>
    public MockArray<T> GetSubArray(int rowIndex)
    {
        if (_dimensions.Length < 2)
        {
            // 1-D fall-back: return a copy of the whole array
            var copy = new MockArray<T>(default!, _items.Length);
            for (int i = 0; i < _items.Length; i++)
                copy[i] = _items[i];
            return copy;
        }

        // Row stride = product of all dimensions except the first
        int stride = 1;
        for (int d = 1; d < _dimensions.Length; d++)
            stride *= _dimensions[d];

        int start = rowIndex * stride;
        var result = new MockArray<T>(default!, stride);
        for (int i = 0; i < stride; i++)
            result[i] = _items[start + i];
        return result;
    }

    public void Clear()
    {
        for (int i = 0; i < _items.Length; i++)
            _items[i] = _factory != null ? _factory() : default!;
    }

    public void Clear(int[] indexes)
    {
        _items[FlatIndex(indexes)] = _factory != null ? _factory() : default!;
    }

    public void ClearReference() => Clear();
    public void ClearReference(int[] indexes) => Clear(indexes);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ── flat-index helpers ────────────────────────────────────────────────────

    private int FlatIndex(int r, int c)
    {
        // dim[1] is the column count
        int colCount = _dimensions.Length > 1 ? _dimensions[1] : 1;
        return r * colCount + c;
    }

    private int FlatIndex(int x, int y, int z)
    {
        int d1 = _dimensions.Length > 1 ? _dimensions[1] : 1;
        int d2 = _dimensions.Length > 2 ? _dimensions[2] : 1;
        return x * d1 * d2 + y * d2 + z;
    }

    private int FlatIndex(int[] indexes)
    {
        if (indexes.Length == 1) return indexes[0];
        if (indexes.Length == 2) return FlatIndex(indexes[0], indexes[1]);
        if (indexes.Length == 3) return FlatIndex(indexes[0], indexes[1], indexes[2]);

        // General case for higher-dimension arrays
        int flat = 0;
        // Compute strides from innermost dimension outward
        var strides = new int[_dimensions.Length];
        strides[_dimensions.Length - 1] = 1;
        for (int d = _dimensions.Length - 2; d >= 0; d--)
            strides[d] = strides[d + 1] * _dimensions[d + 1];
        for (int d = 0; d < indexes.Length && d < _dimensions.Length; d++)
            flat += indexes[d] * strides[d];
        return flat;
    }
}
