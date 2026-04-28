namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        var resp = EnsureResponse(response);
        if (!resp.ALIsSuccessStatusCode)
            return ThrowHttpError(errorLevel, request?.ALMethod ?? "", request?.ALGetRequestUri ?? "", resp);
        return ThrowNotSupported(errorLevel);
    }

    /// <summary>
    /// BC emits: <c>client.ALGet(DataError, url, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Get(url, response)</c>.
    /// </summary>
    public bool ALGet(DataError errorLevel, string url,
        ByRef<MockHttpResponseMessage> response)
    {
        var resp = EnsureResponse(response);
        if (!resp.ALIsSuccessStatusCode)
            return ThrowHttpError(errorLevel, "GET", url ?? "", resp);
        return ThrowNotSupported(errorLevel);
    }

    /// <summary>
    /// BC emits: <c>client.ALPost(DataError, url, content, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Post(url, content, response)</c>.
    /// </summary>
    public bool ALPost(DataError errorLevel, string url, MockHttpContent content,
        ByRef<MockHttpResponseMessage> response)
    {
        var resp = EnsureResponse(response);
        if (!resp.ALIsSuccessStatusCode)
            return ThrowHttpError(errorLevel, "POST", url ?? "", resp);
        return ThrowNotSupported(errorLevel);
    }

    /// <summary>
    /// BC emits: <c>client.ALPut(DataError, url, content, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Put(url, content, response)</c>.
    /// </summary>
    public bool ALPut(DataError errorLevel, string url, MockHttpContent content,
        ByRef<MockHttpResponseMessage> response)
    {
        var resp = EnsureResponse(response);
        if (!resp.ALIsSuccessStatusCode)
            return ThrowHttpError(errorLevel, "PUT", url ?? "", resp);
        return ThrowNotSupported(errorLevel);
    }

    /// <summary>
    /// BC emits: <c>client.ALDelete(DataError, url, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Delete(url, response)</c>.
    /// </summary>
    public bool ALDelete(DataError errorLevel, string url,
        ByRef<MockHttpResponseMessage> response)
    {
        var resp = EnsureResponse(response);
        if (!resp.ALIsSuccessStatusCode)
            return ThrowHttpError(errorLevel, "DELETE", url ?? "", resp);
        return ThrowNotSupported(errorLevel);
    }

    /// <summary>
    /// BC emits: <c>client.ALPatch(DataError, url, content, ByRef&lt;MockHttpResponseMessage&gt;)</c>
    /// for <c>HttpClient.Patch(url, content, response)</c>.
    /// </summary>
    public bool ALPatch(DataError errorLevel, string url, MockHttpContent content,
        ByRef<MockHttpResponseMessage> response)
    {
        var resp = EnsureResponse(response);
        if (!resp.ALIsSuccessStatusCode)
            return ThrowHttpError(errorLevel, "PATCH", url ?? "", resp);
        return ThrowNotSupported(errorLevel);
    }

    /// <summary>
    /// Timeout property stub (milliseconds). No-op in standalone mode.
    /// BC's HttpClient.Timeout is Integer (milliseconds), but AL allows assigning
    /// a Duration value directly (Duration → Integer implicit conversion in AL).
    /// The BC compiler emits <c>client.ALTimeout = navDuration</c> without any explicit
    /// cast, so this property uses NavDuration to accept either form.
    /// NavDuration has implicit conversions from long and to long, covering Integer
    /// assignments too (ALCompiler.ToInt32(NavDuration) resolves via long overload).
    /// </summary>
    public NavDuration ALTimeout { get; set; } = 30L;

    /// <summary>DefaultRequestHeaders property stub.</summary>
    public MockHttpHeaders ALDefaultRequestHeaders => _defaultHeaders;
    private MockHttpHeaders _defaultHeaders = new();

    // Backing field for UseDefaultNetworkWindowsAuthentication state.
    private bool _useDefaultNetworkWindowsAuthentication = false;

    /// <summary>
    /// UseDefaultNetworkWindowsAuthentication() — BC emits as a method call
    /// (0 AL args → 1 C# arg: DataError).  Declares Windows auth should be used.
    /// No-op in standalone mode; records the flag for <see cref="ALAssign"/>.
    /// Fixes CS1955 that arose when this was declared as a property (issue #1532).
    /// </summary>
    public bool ALUseDefaultNetworkWindowsAuthentication(DataError errorLevel = DataError.ThrowError)
    {
        _useDefaultNetworkWindowsAuthentication = true;
        return true;
    }

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
        ALTimeout = 30L;
        _useDefaultNetworkWindowsAuthentication = false;
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

    /// <summary>UseWindowsAuthentication(username, password) — SecretText overload (issue #1533).</summary>
    public void ALUseWindowsAuthentication(DataError errorLevel, NavSecretText username, NavSecretText password) { }

    /// <summary>UseWindowsAuthentication(username, password, domain) — no-op stub.</summary>
    public void ALUseWindowsAuthentication(DataError errorLevel, NavText username, NavText password, NavText domain) { }

    /// <summary>UseWindowsAuthentication(username, password, domain) — SecretText overload (issue #1533).</summary>
    public void ALUseWindowsAuthentication(DataError errorLevel, NavSecretText username, NavSecretText password, NavSecretText domain) { }

    /// <summary>AddCertificate(thumbprint) — no-op stub.</summary>
    public void ALAddCertificate(DataError errorLevel, NavText thumbprint) { }

    /// <summary>AddCertificate(thumbprint) — SecretText overload (issue #1533).</summary>
    public void ALAddCertificate(DataError errorLevel, NavSecretText thumbprint) { }

    /// <summary>AddCertificate(thumbprint, password) — no-op stub.</summary>
    public void ALAddCertificate(DataError errorLevel, NavText thumbprint, NavText password) { }

    /// <summary>AddCertificate(thumbprint, password) — SecretText overload (issue #1533).</summary>
    public void ALAddCertificate(DataError errorLevel, NavSecretText thumbprint, NavSecretText password) { }

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
        _useDefaultNetworkWindowsAuthentication = other._useDefaultNetworkWindowsAuthentication;
        ALUseServerCertificateValidation = other.ALUseServerCertificateValidation;
    }

    private static MockHttpResponseMessage EnsureResponse(ByRef<MockHttpResponseMessage> response)
    {
        response.Value ??= new MockHttpResponseMessage();
        var testResponse = MockTestHttpResponseMessage.ConsumeLastConfigured();
        if (testResponse != null)
            response.Value.ALAssign(testResponse);
        return response.Value;
    }

    private static bool ThrowNotSupported(DataError errorLevel)
    {
        if (errorLevel == DataError.ThrowError)
            throw new NotSupportedException(HttpNotSupportedMessage);
        return false;
    }

    private static bool ThrowHttpError(DataError errorLevel, string method, string uri, MockHttpResponseMessage response)
    {
        if (errorLevel == DataError.ThrowError)
            throw new Exception(BuildErrorEnvelope(method, uri, response));
        return false;
    }

    private static string BuildErrorEnvelope(string method, string uri, MockHttpResponseMessage response)
    {
        var requestObj = new JObject
        {
            ["Method"] = method ?? string.Empty,
            ["URI"] = uri ?? string.Empty
        };
        var responseObj = new JObject
        {
            ["HTTP Status Code"] = response.ALHttpStatusCode,
            ["Reason Phrase"] = response.ALReasonPhrase ?? string.Empty,
            ["Body"] = BuildBodyToken(response.ALContent?.GetText() ?? string.Empty)
        };
        var root = new JObject
        {
            ["Request"] = requestObj,
            ["Response"] = responseObj
        };
        return "HTTP Request resulted in an error." + root.ToString(Formatting.None);
    }

    private static JToken BuildBodyToken(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return JValue.CreateString(string.Empty);
        try
        {
            return JToken.Parse(body);
        }
        catch (JsonReaderException)
        {
            return JValue.CreateString(body);
        }
    }
}
