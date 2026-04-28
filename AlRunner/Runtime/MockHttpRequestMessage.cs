namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Stub for <c>NavHttpRequestMessage</c> / AL's <c>HttpRequestMessage</c> variable.
///
/// BC emits <c>new NavHttpRequestMessage(this)</c> in scope-class field initialisers.
/// The real ctor takes an ITreeObject parent whose <c>.Tree</c> must not be null.
/// The rewriter rewrites the type to <c>MockHttpRequestMessage</c> and strips the arg.
///
/// Stores request properties in memory. The actual HTTP send is performed by
/// <see cref="MockHttpClient"/> which throws <see cref="NotSupportedException"/>.
/// </summary>
public class MockHttpRequestMessage
{
    private MockHttpHeaders _headers = new();
    private MockHttpContent _content = new();
    private Dictionary<string, MockCookie> _cookies = new(StringComparer.OrdinalIgnoreCase);
    private string? _secretUri = null;

    public MockHttpRequestMessage() { }

    /// <summary>HTTP method (GET, POST, PUT, DELETE, PATCH, etc.).</summary>
    public string ALMethod { get; set; } = "GET";

    /// <summary>Request URI. BC emits <c>request.ALGetRequestUri</c> for get.</summary>
    public string ALGetRequestUri { get; set; } = "";

    /// <summary>
    /// BC emits: <c>request.ALSetRequestUri(DataError, url)</c>
    /// for <c>HttpRequestMessage.SetRequestUri(url)</c>.
    /// </summary>
    public bool ALSetRequestUri(DataError errorLevel, string uri)
    {
        ALGetRequestUri = uri;
        return true;
    }

    /// <summary>Request content. BC emits <c>request.ALContent</c> get/set.</summary>
    public MockHttpContent ALContent
    {
        get => _content;
        set => _content = value ?? new();
    }

    /// <summary>
    /// BC emits: <c>request.ALGetHeaders(DataError, ByRef&lt;MockHttpHeaders&gt;)</c>
    /// for <c>HttpRequestMessage.GetHeaders(headers)</c>.
    /// Sets the out-parameter to the stored headers instance.
    /// </summary>
    public bool ALGetHeaders(DataError errorLevel, ByRef<MockHttpHeaders> headers)
    {
        headers.Value = _headers;
        return true;
    }

    /// <summary>
    /// ALAssign for the ByRef pattern.
    /// </summary>
    public void ALAssign(MockHttpRequestMessage other)
    {
        if (other == null) return;
        ALMethod = other.ALMethod;
        ALGetRequestUri = other.ALGetRequestUri;
        _content = other._content;
        _headers = other._headers;
    }

    // ── Cookie methods ────────────────────────────────────────────────────────

    /// <summary>
    /// BC emits: <c>req.ALSetCookie(DataError, name, value)</c>
    /// for <c>HttpRequestMessage.SetCookie(name, value)</c>.
    /// Stores a MockCookie keyed by name (case-insensitive) and returns true.
    /// </summary>
    public bool ALSetCookie(DataError errorLevel, string name, string value)
    {
        var cookie = new MockCookie { ALName = name, ALValue = value };
        _cookies[name] = cookie;
        return true;
    }

    /// <summary>
    /// BC emits: <c>req.ALSetCookie(DataError, cookie)</c>
    /// for <c>HttpRequestMessage.SetCookie(cookie)</c>.
    /// Stores the cookie keyed by its name and returns true.
    /// </summary>
    public bool ALSetCookie(DataError errorLevel, MockCookie cookie)
    {
        if (cookie == null) return false;
        var stored = new MockCookie();
        stored.ALAssign(cookie);
        _cookies[stored.ALName] = stored;
        return true;
    }

    /// <summary>
    /// BC emits: <c>req.ALGetCookie(DataError, name, ByRef&lt;MockCookie&gt;)</c>
    /// for <c>HttpRequestMessage.GetCookie(name, cookie)</c>.
    /// Returns true and sets the out-param when the cookie exists; false otherwise.
    /// </summary>
    public bool ALGetCookie(DataError errorLevel, string name, ByRef<MockCookie> cookie)
    {
        if (_cookies.TryGetValue(name, out var found))
        {
            cookie.Value = found;
            return true;
        }
        cookie.Value = new MockCookie();
        return false;
    }

    /// <summary>
    /// BC emits: <c>req.ALRemoveCookie(DataError, name)</c>
    /// for <c>HttpRequestMessage.RemoveCookie(name)</c>.
    /// Removes the named cookie; returns true if removed.
    /// </summary>
    public bool ALRemoveCookie(DataError errorLevel, string name)
        => _cookies.Remove(name);

    /// <summary>
    /// BC emits: <c>req.ALGetCookieNames()</c>
    /// for <c>HttpRequestMessage.GetCookieNames()</c>.
    /// Returns a NavList of all stored cookie names.
    /// </summary>
    public NavList<NavText> ALGetCookieNames()
    {
        var list = NavList<NavText>.Default;
        foreach (var key in _cookies.Keys)
            list.ALAdd(new NavText(key));
        return list;
    }

    // ── Secret URI methods ────────────────────────────────────────────────────

    /// <summary>
    /// BC emits: <c>req.ALSetSecretRequestUri(DataError, uri)</c>
    /// for <c>HttpRequestMessage.SetSecretRequestUri(uri)</c>.
    /// Stores the secret URI string (SecretText is unwrapped to string by BC).
    /// </summary>
    public void ALSetSecretRequestUri(DataError errorLevel, string uri)
    {
        _secretUri = uri;
    }

    /// <summary>
    /// BC emits: <c>req.ALGetSecretRequestUri(DataError, ByRef&lt;string&gt;)</c>
    /// for <c>HttpRequestMessage.GetSecretRequestUri(uri)</c>.
    /// Returns true when a secret URI has been set.
    /// </summary>
    public bool ALGetSecretRequestUri(DataError errorLevel, ByRef<string> uri)
    {
        if (_secretUri != null)
        {
            uri.Value = _secretUri;
            return true;
        }
        uri.Value = "";
        return false;
    }

    /// <summary>No-op Clear.</summary>
    public void Clear() { }
}
