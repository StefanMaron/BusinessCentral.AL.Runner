using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Runtime;

/// <summary>
/// Standalone replacement for <c>NavObjectList&lt;T&gt;</c>. BC's real type
/// constrains <c>T : ITreeObject</c> and needs a valid parent with a
/// non-null Tree handler; neither is available without a service tier.
/// MockObjectList exposes only the surface AL transpiled code actually uses
/// (ALAdd, ALCount, ALGet, ALContains, ALRemove, iteration) on a plain
/// List&lt;T&gt;.
/// </summary>
public class MockObjectList<T> : IEnumerable<T>
{
    private readonly List<T> _items = new();

    public MockObjectList() { }

    // BC-style constructor stand-in — rewriter drops any ctor argument.
    public MockObjectList(object? _) { }

    public void ALAdd(T item) => _items.Add(item);

    public void ALAddRange(IEnumerable<T> items) => _items.AddRange(items);

    public int ALCount => _items.Count;

    public int Count => _items.Count;

    public T ALGet(int oneBasedIndex) => _items[oneBasedIndex - 1];

    /// <summary>
    /// ALGet with out-parameter (DataError, index, handle) — used by lists of ITreeObject
    /// values (e.g., List of [Codeunit]) where the caller passes the handle directly.
    /// </summary>
    public bool ALGet(DataError errorLevel, int oneBasedIndex, T handle)
    {
        if (oneBasedIndex < 1 || oneBasedIndex > _items.Count)
        {
            if (errorLevel == DataError.ThrowError)
                throw new InvalidOperationException(
                    $"The list does not contain an element at position {oneBasedIndex}.");
            return false;
        }

        var stored = _items[oneBasedIndex - 1];
        if (handle != null && stored != null)
        {
            var method = handle.GetType().GetMethod(
                "ALAssign",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { stored.GetType() },
                null);
            method?.Invoke(handle, new object[] { stored });
        }

        return true;
    }

    public bool ALContains(T item) => _items.Contains(item);

    public bool ALRemove(T item) => _items.Remove(item);

    /// <summary>
    /// ALAssign — BC emits <c>list.ALAssign(other)</c> for the AL <c>:=</c>
    /// assignment operator on <c>List of [T]</c> variables.  Clears the current
    /// list and copies all items from <paramref name="other"/>.
    /// </summary>
    public void ALAssign(MockObjectList<T> other)
    {
        _items.Clear();
        _items.AddRange(other._items);
    }

    public void Clear() => _items.Clear();

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static MockObjectList<T> Default => new();
}
