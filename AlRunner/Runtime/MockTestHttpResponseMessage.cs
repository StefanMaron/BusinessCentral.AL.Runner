namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Stub for <c>NavTestHttpResponseMessage</c> / AL's <c>TestHttpResponseMessage</c> variable.
///
/// BC emits <c>new NavTestHttpResponseMessage(this)</c> in scope-class field initialisers.
/// The rewriter renames the type to <c>MockTestHttpResponseMessage</c> and strips the arg.
///
/// Default state mirrors a successful HTTP 200 OK response.
/// </summary>
public class MockTestHttpResponseMessage
{
    public MockTestHttpResponseMessage() { }

    /// <summary>HTTP status code. Default is 200 (OK).</summary>
    public int ALHttpStatusCode { get; set; } = 200;

    /// <summary>True when status code is in the 200–299 range.</summary>
    public bool ALIsSuccessfulRequest => ALHttpStatusCode >= 200 && ALHttpStatusCode < 300;

    /// <summary>HTTP reason phrase (e.g. "OK", "Not Found").</summary>
    public string ALReasonPhrase { get; set; } = "OK";

    /// <summary>Response body content.</summary>
    public MockHttpContent ALContent { get; set; } = new();

    /// <summary>Response headers.</summary>
    public MockHttpHeaders ALHeaders { get; set; } = new();

    /// <summary>Always false — environment blocking is not applicable in test mode.</summary>
    public bool ALIsBlockedByEnvironment => false;
}
