namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Stub for BC's <c>TestHttpRequestMessage</c> / <c>NavTestHttpRequestMessage</c> type.
///
/// BC emits <c>new NavTestHttpRequestMessage(this)</c> in scope-class field initialisers.
/// The rewriter renames the type to <c>MockTestHttpRequestMessage</c> and strips the arg.
///
/// Provides in-memory defaults for all four gap properties:
///   Path, RequestType — empty string by default
///   HasSecretUri — always false in standalone mode
///   QueryParameters — returns empty Dictionary of [Text,Text] (no URI parsing in standalone)
/// </summary>
public class MockTestHttpRequestMessage
{
    public MockTestHttpRequestMessage() { }

    /// <summary>HTTP request path (e.g. "/api/items"). Default is empty.</summary>
    public NavText ALPath { get; set; } = NavText.Empty;

    /// <summary>HTTP method (e.g. "GET", "POST"). Default is empty.</summary>
    public NavText ALRequestType { get; set; } = NavText.Empty;

    /// <summary>Always false — no secret URI support in standalone mode.</summary>
    public bool ALHasSecretUri => false;

    /// <summary>
    /// BC emits <c>req.ALQueryParameters</c> (property) for
    /// <c>TestHttpRequestMessage.QueryParameters()</c>.
    /// Returns an empty dictionary — no URI query-string parsing in standalone mode.
    /// </summary>
    public NavDictionary<NavText, NavText> ALQueryParameters
        => NavDictionary<NavText, NavText>.Default;
}
