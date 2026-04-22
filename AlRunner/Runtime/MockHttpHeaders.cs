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
    /// for <c>HttpHeaders.Add(key, value)</c> (Text overload).
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
    /// BC emits: <c>headers.ALAdd(DataError, key, secretValue)</c>
    /// for <c>HttpHeaders.Add(key, SecretText)</c>.
    /// In standalone mode secrets are treated as plain text — the value
    /// is extracted via <see cref="AlCompat.Unwrap"/> and stored normally.
    /// </summary>
    public void ALAdd(DataError errorLevel, string key, NavSecretText secretValue)
        => ALAdd(errorLevel, key, (string)AlCompat.Unwrap(secretValue));

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

    /// <summary>Exposes stored header names for MockJsonHelper.Keys() interception.</summary>
    public IEnumerable<string> HeaderNames => _headers.Keys;

    /// <summary>Removes all headers.</summary>
    public void Clear() => _headers.Clear();

    /// <summary>
    /// BC emits: <c>headers.ALClear()</c>
    /// for <c>HttpHeaders.Clear()</c>.
    /// </summary>
    public void ALClear() => _headers.Clear();

    /// <summary>
    /// BC emits: <c>headers.ALTryAddWithoutValidation(DataError, name, value)</c>
    /// for <c>HttpHeaders.TryAddWithoutValidation(name, value)</c> (Text overload).
    /// Adds the header without format validation; always succeeds.
    /// </summary>
    public bool ALTryAddWithoutValidation(DataError errorLevel, NavText name, NavText value)
    {
        ALAdd(errorLevel, (string)name, (string)value);
        return true;
    }

    /// <summary>
    /// Overload for <c>HttpHeaders.TryAddWithoutValidation(name, SecretText)</c>.
    /// BC emits <c>ALTryAddWithoutValidation(DataError, string-literal, NavSecretText)</c>
    /// when the header name is a text literal and the value is a SecretText — resolving
    /// both the <c>string → NavText</c> (#1091) and <c>NavSecretText → NavText</c> (#1086)
    /// type mismatches. In standalone mode secrets are treated as plain text.
    /// </summary>
    public bool ALTryAddWithoutValidation(DataError errorLevel, string name, NavSecretText secretValue)
    {
        ALAdd(errorLevel, name, (string)AlCompat.Unwrap(secretValue));
        return true;
    }

    /// <summary>
    /// BC emits: <c>headers.ALIsHeaderValueSecret(name)</c>
    /// for <c>HttpHeaders.ContainsSecret(name)</c>.
    /// Plain in-memory headers are never secret — always returns false.
    /// </summary>
    public bool ALIsHeaderValueSecret(NavText name) => false;

    /// <summary>
    /// Overload for <c>HttpHeaders.GetSecretValues(name, secrets)</c>:
    /// BC emits <c>ALGetValues(DataError, NavText, NavList&lt;NavSecretText&gt;)</c>.
    /// Plain headers have no secret values; the list is left empty.
    /// </summary>
    public bool ALGetValues(DataError errorLevel, NavText key, NavList<NavSecretText> values)
        => false;
}
