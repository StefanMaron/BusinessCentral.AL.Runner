namespace AlRunner.Runtime;

using System.Collections;

/// <summary>
/// Replacement for NavArray&lt;MockRecordHandle&gt; (and NavArray&lt;INavRecordHandle&gt;).
/// AL arrays of Record variables are transpiled to NavArray&lt;NavRecordHandle&gt; with a Factory2.
/// Since IFactory&lt;T&gt; is internal to Nav.Ncl.dll, we replace the entire NavArray usage
/// with this simple wrapper.
///
/// Indexing convention: BC emits 0-based index accesses for record arrays
/// (i.e. <c>recArr[alIdx - 1]</c> where alIdx is the 1-based AL index).
/// This indexer therefore uses 0-based semantics to match the emitted code.
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
    /// 0-based indexer — BC emits <c>recArr[alIdx - 1]</c> (subtracting the AL 1-base),
    /// so this indexer receives a 0-based integer.
    /// </summary>
    public MockRecordHandle this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
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

    /// <summary>
    /// AL's Clear(RecordArray[i]) — BC emits <c>navArray.Clear(alIdx - 1)</c> where
    /// <paramref name="index"/> is the 0-based element index.
    /// Resets the single element to its default (empty) state.
    /// </summary>
    public void Clear(int index)
    {
        if (index >= 0 && index < _items.Length)
            _items[index].Clear();
    }

    public IEnumerator<MockRecordHandle> GetEnumerator()
    {
        return ((IEnumerable<MockRecordHandle>)_items).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
