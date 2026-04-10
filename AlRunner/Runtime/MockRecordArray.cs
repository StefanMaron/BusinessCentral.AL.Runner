namespace AlRunner.Runtime;

using System.Collections;

/// <summary>
/// Replacement for NavArray&lt;MockRecordHandle&gt; (and NavArray&lt;INavRecordHandle&gt;).
/// AL arrays of Record variables are transpiled to NavArray&lt;NavRecordHandle&gt; with a Factory2.
/// Since IFactory&lt;T&gt; is internal to Nav.Ncl.dll, we replace the entire NavArray usage
/// with this simple wrapper that provides 1-based indexing like AL arrays.
/// </summary>
public class MockRecordArray : IEnumerable<MockRecordHandle>
{
    private readonly MockRecordHandle[] _items;
    private readonly int _tableId;

    public MockRecordArray(int tableId, int length)
    {
        _tableId = tableId;
        _items = new MockRecordHandle[length];
        for (int i = 0; i < length; i++)
            _items[i] = new MockRecordHandle(tableId);
    }

    /// <summary>
    /// 1-based indexer matching AL array semantics.
    /// AL arrays are 1-based: x[1], x[2], etc.
    /// </summary>
    public MockRecordHandle this[int index]
    {
        get => _items[index - 1];
        set => _items[index - 1] = value;
    }

    public int Length => _items.Length;

    /// <summary>AL's ARRAYLEN function — returns the number of elements.</summary>
    public int ArrayLen() => _items.Length;

    /// <summary>Clear all elements in the array (re-initialize with fresh handles).</summary>
    public void Clear()
    {
        for (int i = 0; i < _items.Length; i++)
            _items[i] = new MockRecordHandle(_tableId);
    }

    public IEnumerator<MockRecordHandle> GetEnumerator()
    {
        return ((IEnumerable<MockRecordHandle>)_items).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
