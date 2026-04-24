namespace AlRunner.Runtime;

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Standalone replacement for <c>NavObjectDictionary&lt;TKey, TValue&gt;</c>.
/// BC's real type constrains <c>TValue : ITreeObject, IALAssignable&lt;TValue&gt;</c>
/// and requires a valid parent with a non-null Tree handler; neither is available
/// in standalone mode.
///
/// MockObjectDictionary exposes the same API surface the transpiled code calls
/// (ALAdd, ALContainsKey, ALGet, ALRemove, ALSet, ALCount, ALKeys, ALValues, Clear)
/// on a plain <c>Dictionary&lt;TKey, TValue&gt;</c> without any ITreeObject constraint.
///
/// This resolves issues #1239, #1240, #1241 where
/// <c>Dictionary of [Guid, Codeunit X]</c> (and similar) caused CS0311/CS1503
/// errors because <c>MockCodeunitHandle</c> does not implement <c>ITreeObject</c>.
/// </summary>
public class MockObjectDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _dict = new();

    /// <summary>
    /// Default constructor — no ITreeObject parent needed.
    /// </summary>
    public MockObjectDictionary() { }

    /// <summary>
    /// BC-style constructor stand-in — rewriter strips the ITreeObject parent argument.
    /// </summary>
    public MockObjectDictionary(object? _) { }

    // -----------------------------------------------------------------------
    // AL API
    // -----------------------------------------------------------------------

    /// <summary>Number of entries.</summary>
    public int ALCount => _dict.Count;

    /// <summary>
    /// ALAdd — adds a key/value pair. Throws if the key already exists
    /// (mirrors NavObjectDictionary behaviour when DataError = ThrowError).
    /// </summary>
    public bool ALAdd(DataError errorLevel, TKey key, TValue value)
    {
        if (_dict.ContainsKey(key))
        {
            if (errorLevel == DataError.ThrowError)
                throw new InvalidOperationException(
                    $"An entry with key '{key}' is already present in the dictionary.");
            return false;
        }
        _dict[key] = value;
        return true;
    }

    /// <summary>ALContainsKey — returns true if the key is present.</summary>
    public bool ALContainsKey(TKey key) => _dict.ContainsKey(key);

    /// <summary>
    /// ALGet — retrieves the value by key; returns true if found.
    /// The stored value is assigned into <paramref name="handle"/> via <c>ALAssign</c>
    /// (reflection-based) so the caller's variable is updated, matching the
    /// NavObjectDictionary ITreeObject/IALAssignable out-parameter contract.
    ///
    /// The generated code passes the AL variable directly (not wrapped in ByRef&lt;T&gt;)
    /// because MockCodeunitHandle is already a reference type and the BC compiler
    /// uses ALAssign semantics for ITreeObject-typed out-parameters.
    /// </summary>
    public bool ALGet(DataError errorLevel, TKey key, TValue handle)
    {
        if (!_dict.TryGetValue(key, out var stored))
            return false;

        // Assign the retrieved value into the caller's handle via ALAssign
        // (mirrors NavObjectDictionary's IALAssignable<T>.ALAssign pattern).
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

    /// <summary>ALGet — retrieves the value by key; throws if not found.</summary>
    public TValue ALGet(TKey key)
    {
        if (!_dict.TryGetValue(key, out var value))
            throw new InvalidOperationException(
                $"The key '{key}' is not present in the dictionary.");
        return value;
    }

    /// <summary>ALRemove — removes the key; returns true if it was present.</summary>
    public bool ALRemove(TKey key) => _dict.Remove(key);

    /// <summary>ALSet — adds or overwrites the value for a key.</summary>
    public void ALSet(TKey key, TValue value) => _dict[key] = value;

    /// <summary>
    /// ALSet with old-value out-param — sets key/value and returns the previous
    /// value via <paramref name="oldValue"/> wrapper. Returns true if the key existed.
    /// </summary>
    public bool ALSet(TKey key, TValue value, ByRef<TValue> oldValue)
    {
        bool existed = _dict.TryGetValue(key, out var prev);
        if (existed && oldValue != null)
            oldValue.Value = prev!;
        _dict[key] = value;
        return existed;
    }

    /// <summary>
    /// ALKeys — returns a NavList of all keys (matches <c>Dictionary.Keys()</c> in AL).
    /// </summary>
    public NavList<TKey> ALKeys
    {
        get
        {
            var list = NavList<TKey>.Default;
            foreach (var k in _dict.Keys)
                list.ALAdd(k);
            return list;
        }
    }

    /// <summary>
    /// ALValues — returns a MockObjectList of all values.
    /// Mirrors NavObjectDictionary.ALValues which returns NavObjectList&lt;TValue&gt;.
    /// </summary>
    public MockObjectList<TValue> ALValues
    {
        get
        {
            var list = new MockObjectList<TValue>();
            foreach (var v in _dict.Values)
                list.ALAdd(v);
            return list;
        }
    }

    /// <summary>Clear all entries.</summary>
    public void Clear() => _dict.Clear();

    /// <summary>Default factory — matches NavObjectDictionary.Default pattern.</summary>
    public static MockObjectDictionary<TKey, TValue> Default => new();

    // IEnumerable
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dict.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _dict.GetEnumerator();
}
