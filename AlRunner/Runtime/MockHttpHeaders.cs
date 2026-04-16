namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Stub for <c>NavHttpHeaders</c> / AL's <c>HttpHeaders</c> variable.
///
/// BC emits <c>NavHttpHeaders.Default</c> for default-initialised header
/// variables. After the rewriter renames the type this becomes
/// <c>MockHttpHeaders.Default</c>. The <see cref="Default"/> static property
/// returns a fresh instance.
///
/// Maintains an in-memory dictionary of header name → values.
/// </summary>
public class MockHttpHeaders
{
    private readonly Dictionary<string, List<string>> _headers = new(StringComparer.OrdinalIgnoreCase);

    public MockHttpHeaders() { }

    /// <summary>
    /// Static factory used by BC-generated field initialisers:
    /// <c>NavHttpHeaders.Default</c> → <c>MockHttpHeaders.Default</c>.
    /// </summary>
    public static MockHttpHeaders Default => new();

    /// <summary>
    /// BC emits: <c>headers.ALAdd(DataError, key, value)</c>
    /// for <c>HttpHeaders.Add(key, value)</c>.
    /// </summary>
    public void ALAdd(DataError errorLevel, string key, string value)
    {
        if (!_headers.TryGetValue(key, out var list))
        {
            list = new List<string>();
            _headers[key] = list;
        }
        list.Add(value);
    }

    /// <summary>
    /// BC emits: <c>headers.ALContains(key)</c>
    /// for <c>HttpHeaders.Contains(key)</c>.
    /// </summary>
    public bool ALContains(string key)
    {
        return _headers.ContainsKey(key);
    }

    /// <summary>
    /// BC emits: <c>headers.ALRemove(DataError, key)</c>
    /// for <c>HttpHeaders.Remove(key)</c>.
    /// </summary>
    public bool ALRemove(DataError errorLevel, string key)
    {
        return _headers.Remove(key);
    }

    /// <summary>
    /// BC emits: <c>headers.ALGetValues(key, ByRef&lt;...&gt;)</c>
    /// for <c>HttpHeaders.GetValues(key, values)</c>.
    /// Returns true if the key exists; values is a placeholder array.
    /// </summary>
    public bool ALGetValues(string key, object values)
    {
        return _headers.ContainsKey(key);
    }

    /// <summary>
    /// Overload emitted when the caller declares <c>array[N] of Text</c>.
    /// BC emits <c>ALGetValues(DataError, key, MockArray&lt;NavText&gt;)</c>.
    /// Populates the array with stored header values.
    /// </summary>
    public bool ALGetValues(DataError errorLevel, string key, MockArray<NavText> values)
    {
        if (!_headers.TryGetValue(key, out var list) || list.Count == 0) return false;
        int count = Math.Min(values.Length, list.Count);
        for (int i = 0; i < count; i++)
            values[i] = new NavText(0, list[i]);
        return true;
    }

    /// <summary>Number of distinct header names.</summary>
    public int ALCount => _headers.Count;

    /// <summary>Removes all headers.</summary>
    public void Clear()
    {
        _headers.Clear();
    }
}
