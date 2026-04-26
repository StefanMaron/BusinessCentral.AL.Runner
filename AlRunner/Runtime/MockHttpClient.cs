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

    // ── Configuration methods (issue #732) ────────────────────────────────

    private string _baseAddress = string.Empty;

    /// <summary>GetBaseAddress — returns the stored base URL (property access in generated C#).</summary>
    public NavText ALGetBaseAddress => new NavText(_baseAddress);

    /// <summary>SetBaseAddress — stores the base URL.</summary>
    public void ALSetBaseAddress(DataError errorLevel, NavText url) => _baseAddress = (string)url;

    /// <summary>
    /// Clear — resets all HttpClient state to defaults.
    /// Called via AL instance method <c>client.Clear()</c> (BC emits <c>client.ALClear()</c>)
    /// and via AL global <c>Clear(client)</c> (BC emits <c>ALSystemVariable.Clear(client)</c>
    /// which the rewriter transforms to <c>client.Clear()</c> — issue #1334).
    /// </summary>
    public void Clear()
    {
        _baseAddress = string.Empty;
        _defaultHeaders = new MockHttpHeaders();
        ALTimeout = 30;
        ALUseDefaultNetworkWindowsAuthentication = false;
        ALUseServerCertificateValidation = false;
    }

    /// <summary>ALClear — BC instance-method variant; delegates to <see cref="Clear"/>.</summary>
    public void ALClear() => Clear();

    /// <summary>UseResponseCookies — stores the flag (emitted as method, no DataError).</summary>
    public void ALUseResponseCookies(bool value) { }

    /// <summary>UseServerCertificateValidation — emitted as property setter/getter.</summary>
    public bool ALUseServerCertificateValidation { get; set; }

    /// <summary>UseWindowsAuthentication(username, password) — no-op stub.</summary>
    public void ALUseWindowsAuthentication(DataError errorLevel, NavText username, NavText password) { }

    /// <summary>UseWindowsAuthentication(username, password, domain) — no-op stub.</summary>
    public void ALUseWindowsAuthentication(DataError errorLevel, NavText username, NavText password, NavText domain) { }

    /// <summary>AddCertificate(thumbprint) — no-op stub.</summary>
    public void ALAddCertificate(DataError errorLevel, NavText thumbprint) { }

    /// <summary>AddCertificate(thumbprint, password) — no-op stub.</summary>
    public void ALAddCertificate(DataError errorLevel, NavText thumbprint, NavText password) { }

    // ── ALAssign (issue #1447) ─────────────────────────────────────────────

    /// <summary>
    /// ALAssign — copy all observable state from <paramref name="other"/> into this instance.
    /// BC emits <c>target.ALAssign(source)</c> for the AL assignment <c>target := source</c>
    /// and for the ByRef pattern <c>ByRef&lt;MockHttpClient&gt;(() =&gt; c, v =&gt; c.ALAssign(v))</c>
    /// used when an HttpClient is passed by var (e.g. <c>var Client: HttpClient</c>).
    /// Covers the 4 occurrences surfaced by issue #1447.
    /// </summary>
    public void ALAssign(MockHttpClient other)
    {
        if (other == null) return;
        _baseAddress = other._baseAddress;
        _defaultHeaders = other._defaultHeaders;
        ALTimeout = other.ALTimeout;
        ALUseDefaultNetworkWindowsAuthentication = other.ALUseDefaultNetworkWindowsAuthentication;
        ALUseServerCertificateValidation = other.ALUseServerCertificateValidation;
    }
}
