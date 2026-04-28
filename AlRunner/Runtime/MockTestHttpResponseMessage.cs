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
public class MockTestHttpResponseMessage : MockHttpResponseMessage
{
    internal static MockTestHttpResponseMessage? LastConfigured { get; private set; }

    public MockTestHttpResponseMessage()
    {
        LastConfigured = this;
    }

    internal static MockTestHttpResponseMessage? ConsumeLastConfigured()
    {
        var last = LastConfigured;
        LastConfigured = null;
        return last;
    }

    /// <summary>True when status code is in the 200–299 range.</summary>
    public bool ALIsSuccessfulRequest => ALIsSuccessStatusCode;
}
