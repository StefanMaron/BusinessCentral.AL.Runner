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

    public MockHttpRequestMessage() { }

    /// <summary>HTTP method (GET, POST, PUT, DELETE, PATCH, etc.).</summary>
    public string ALMethod { get; set; } = "GET";

    /// <summary>Request URI. BC emits <c>request.ALGetRequestUri</c> for get.</summary>
    public string ALGetRequestUri { get; set; } = "";

    /// <summary>
    /// BC emits: <c>request.ALSetRequestUri(DataError, url)</c>
    /// for <c>HttpRequestMessage.SetRequestUri(url)</c>.
    /// </summary>
    public void ALSetRequestUri(DataError errorLevel, string uri)
    {
        ALGetRequestUri = uri;
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
    public void ALGetHeaders(DataError errorLevel, ByRef<MockHttpHeaders> headers)
    {
        headers.Value = _headers;
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

    /// <summary>No-op Clear.</summary>
    public void Clear() { }
}
