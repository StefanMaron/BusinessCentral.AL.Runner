namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Stub for <c>NavHttpClient</c> / AL's <c>HttpClient</c> variable.
///
/// BC emits <c>new NavHttpClient(this)</c> in scope-class field initialisers.
/// The real ctor takes an ITreeObject parent whose <c>.Tree</c> must not be
/// null — impossible in standalone mode. The rewriter rewrites the type to
/// <c>MockHttpClient</c> and strips the ITreeObject arg.
///
/// HTTP calls require the BC service tier and are not available in standalone
/// mode. All network methods throw <see cref="NotSupportedException"/> with
/// a descriptive message recommending AL interface injection.
/// </summary>
public class MockHttpClient
{
    private const string HttpNotSupportedMessage =
        "HTTP calls are not supported by al-runner. " +
        "Use AL interface injection to mock HTTP dependencies. " +
        "See: https://github.com/StefanMaron/BusinessCentral.AL.Runner/blob/main/docs/limitations.md#http--partial-support";

    public MockHttpClient() { }

    /// <summary>
    /// BC emits: <c>client.ALSend(DataError, request, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Send(request, response)</c>.
    /// </summary>
    public bool ALSend(DataError errorLevel, MockHttpRequestMessage request,
        ByRef<MockHttpResponseMessage> response)
    {
        throw new NotSupportedException(HttpNotSupportedMessage);
    }

    /// <summary>
    /// BC emits: <c>client.ALGet(DataError, url, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Get(url, response)</c>.
    /// </summary>
    public bool ALGet(DataError errorLevel, string url,
        ByRef<MockHttpResponseMessage> response)
    {
        throw new NotSupportedException(HttpNotSupportedMessage);
    }

    /// <summary>
    /// BC emits: <c>client.ALPost(DataError, url, content, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Post(url, content, response)</c>.
    /// </summary>
    public bool ALPost(DataError errorLevel, string url, MockHttpContent content,
        ByRef<MockHttpResponseMessage> response)
    {
        throw new NotSupportedException(HttpNotSupportedMessage);
    }

    /// <summary>
    /// BC emits: <c>client.ALPut(DataError, url, content, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Put(url, content, response)</c>.
    /// </summary>
    public bool ALPut(DataError errorLevel, string url, MockHttpContent content,
        ByRef<MockHttpResponseMessage> response)
    {
        throw new NotSupportedException(HttpNotSupportedMessage);
    }

    /// <summary>
    /// BC emits: <c>client.ALDelete(DataError, url, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Delete(url, response)</c>.
    /// </summary>
    public bool ALDelete(DataError errorLevel, string url,
        ByRef<MockHttpResponseMessage> response)
    {
        throw new NotSupportedException(HttpNotSupportedMessage);
    }

    /// <summary>
    /// BC emits: <c>client.ALPatch(DataError, url, content, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Patch(url, content, response)</c>.
    /// </summary>
    public bool ALPatch(DataError errorLevel, string url, MockHttpContent content,
        ByRef<MockHttpResponseMessage> response)
    {
        throw new NotSupportedException(HttpNotSupportedMessage);
    }

    /// <summary>Timeout property stub (seconds). No-op in standalone mode.</summary>
    public int ALTimeout { get; set; } = 30;

    /// <summary>DefaultRequestHeaders property stub.</summary>
    public MockHttpHeaders ALDefaultRequestHeaders => _defaultHeaders;
    private MockHttpHeaders _defaultHeaders = new();

    /// <summary>UseDefaultNetworkWindowsAuthentication property stub.</summary>
    public bool ALUseDefaultNetworkWindowsAuthentication { get; set; }

    /// <summary>No-op — resets nothing since no real connection exists.</summary>
    public void Clear() { }
}
