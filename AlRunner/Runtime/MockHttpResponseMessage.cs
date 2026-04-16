namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Stub for <c>NavHttpResponseMessage</c> / AL's <c>HttpResponseMessage</c> variable.
///
/// BC emits <c>new NavHttpResponseMessage(this)</c> in scope-class field initialisers.
/// The real ctor takes an ITreeObject parent whose <c>.Tree</c> must not be null.
/// The rewriter rewrites the type to <c>MockHttpResponseMessage</c> and strips the arg.
///
/// Default state mirrors a successful HTTP 200 OK response.
/// </summary>
public class MockHttpResponseMessage
{
    public MockHttpResponseMessage() { }

    /// <summary>HTTP status code. Default is 200 (OK).</summary>
    public int ALHttpStatusCode { get; set; } = 200;

    /// <summary>True when status code is in the 200–299 range.</summary>
    public bool ALIsSuccessStatusCode => ALHttpStatusCode >= 200 && ALHttpStatusCode < 300;

    /// <summary>Response body content.</summary>
    public MockHttpContent ALContent { get; set; } = new();

    /// <summary>Response headers.</summary>
    public MockHttpHeaders ALHeaders { get; set; } = new();

    /// <summary>HTTP reason phrase (e.g. "OK", "Not Found").</summary>
    public string ALReasonPhrase { get; set; } = "OK";

    /// <summary>
    /// ALAssign for the ByRef pattern.
    /// BC generates <c>ByRef&lt;MockHttpResponseMessage&gt;(() =&gt; resp, v =&gt; resp.ALAssign(v))</c>.
    /// </summary>
    public void ALAssign(MockHttpResponseMessage other)
    {
        if (other == null) return;
        ALHttpStatusCode = other.ALHttpStatusCode;
        ALContent = other.ALContent;
        ALHeaders = other.ALHeaders;
        ALReasonPhrase = other.ALReasonPhrase;
    }

    /// <summary>No-op Clear.</summary>
    public void Clear() { }

    /// <summary>
    /// BC emits: <c>response.ALIsBlockedByEnvironment</c> (property read)
    /// for <c>HttpResponseMessage.IsBlockedByEnvironment()</c>.
    /// Always false — environment blocking is not applicable in standalone mode.
    /// </summary>
    public bool ALIsBlockedByEnvironment => false;

    /// <summary>
    /// BC emits: <c>response.ALGetCookie(DataError, name, ByRef&lt;MockCookie&gt;)</c>
    /// for <c>HttpResponseMessage.GetCookie(CookieName, var Cookie)</c>.
    /// Always returns false — no cookies are stored in standalone mode.
    /// </summary>
    public bool ALGetCookie(DataError errorLevel, NavText cookieName, ByRef<MockCookie> cookie)
    {
        cookie.Value = new MockCookie();
        return false;
    }

    /// <summary>
    /// BC emits: <c>response.ALGetCookieNames(DataError)</c>
    /// for <c>HttpResponseMessage.GetCookieNames()</c>.
    /// Returns an empty list — no cookies in standalone mode.
    /// </summary>
    public NavList<NavText> ALGetCookieNames(DataError errorLevel)
    {
        return NavList<NavText>.Default;
    }

    /// <summary>Overload without DataError for caller convenience.</summary>
    public NavList<NavText> ALGetCookieNames()
    {
        return NavList<NavText>.Default;
    }
}
