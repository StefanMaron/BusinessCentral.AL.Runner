namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Stub for <c>NavHttpContent</c> / AL's <c>HttpContent</c> variable.
///
/// BC emits <c>new NavHttpContent(this)</c> in scope-class field initialisers.
/// The real ctor takes an ITreeObject parent whose <c>.Tree</c> must not be null.
/// The rewriter rewrites the type to <c>MockHttpContent</c> and strips the arg.
///
/// Stores text-only content in memory. WriteFrom stores text; ReadAs retrieves it.
/// Binary data written via InStream will be UTF-8 decoded on load and re-encoded
/// on read, which may not preserve raw bytes. The stream overloads are redirected
/// by the rewriter through <c>AlCompat.HttpContentLoadFrom</c> /
/// <c>AlCompat.HttpContentReadAs</c>.
/// </summary>
public class MockHttpContent
{
    private string _textContent = "";
    private MockHttpHeaders _headers = new();

    public MockHttpContent() { }

    /// <summary>
    /// BC emits the text variant: <c>content.ALLoadFrom(new NavText(...))</c>
    /// for <c>HttpContent.WriteFrom(Text)</c>. This is called from AlCompat
    /// after the rewriter redirect.
    /// </summary>
    public void ALLoadFrom(NavText text)
    {
        _textContent = text?.ToString() ?? "";
    }

    /// <summary>
    /// BC emits the 2-arg text variant:
    /// <c>content.ALReadAs(DataError, ByRef&lt;NavText&gt;)</c>
    /// for <c>HttpContent.ReadAs(var Text)</c>. Called directly on the mock.
    ///
    /// Returns <c>true</c> (matching the real BC API) so that BC-generated code that
    /// uses <c>if Content.ReadAs(ResponseBodyText) then</c> compiles without CS0019.
    /// BC emits <c>CStmtHit(N) &amp; (content.ALReadAs(...))</c>, which requires the
    /// method to return <c>bool</c>; a <c>void</c> return caused CS0019 (#1250).
    /// </summary>
    public bool ALReadAs(DataError errorLevel, ByRef<NavText> text)
    {
        text.Value = new NavText(_textContent);
        return true;
    }

    /// <summary>
    /// Content headers. BC emits <c>content.ALGetHeaders(errorLevel, byref headers)</c>
    /// as a method call with a ByRef out parameter. The headers collection is shared
    /// with the content so later mutations round-trip.
    /// </summary>
    public bool ALGetHeaders(DataError errorLevel, ByRef<MockHttpHeaders> headers)
    {
        headers.Value = _headers;
        return true;
    }

    /// <summary>
    /// ALAssign for the ByRef pattern and <c>content := response.Content</c>.
    /// </summary>
    public void ALAssign(MockHttpContent other)
    {
        if (other == null) return;
        _textContent = other._textContent;
        _headers = other._headers;
    }

    /// <summary>Returns the currently stored text content.</summary>
    public string GetText() => _textContent;

    /// <summary>Resets content to empty.</summary>
    public void Clear()
    {
        _textContent = "";
        _headers = new();
    }

    /// <summary>
    /// BC emits: <c>content.ALClear()</c> for <c>HttpContent.Clear()</c>.
    /// Resets stored content and headers.
    /// </summary>
    public void ALClear()
    {
        _textContent = "";
        _headers = new();
    }

    /// <summary>
    /// BC emits: <c>content.ALIsSecretContent()</c> for <c>HttpContent.IsSecretContent()</c>.
    /// Always false — the runner has no secret-content support.
    /// </summary>
    public bool ALIsSecretContent() => false;
}
