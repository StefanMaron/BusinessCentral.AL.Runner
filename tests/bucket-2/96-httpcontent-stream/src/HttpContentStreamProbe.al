/// <summary>
/// Probe codeunit for test 96 — NavHttpContent.ALLoadFrom/ALReadAs with InStream/OutStream.
/// When AL code calls HttpContent.WriteFrom(InStream) or HttpContent.ReadAs(var InStream),
/// the BC compiler generates ALLoadFrom(MockInStream) / ALReadAs(this, DataError, ByRef<MockInStream>)
/// after the NavInStream→MockInStream type rename in the rewriter.
/// NavHttpContent.ALLoadFrom expects NavInStream, not MockInStream → CS1503.
/// The fix: rewriter redirects these calls to AlCompat helpers that accept MockInStream.
///
/// WriteBodyFromStream() and ReadBodyIntoStream() exercise the InStream/OutStream variants
/// (present only to prove compilation; never called by tests since HTTP is unsupported).
/// WriteBodyFromText() and GetHeaderValue() are pure-logic helpers that tests DO call.
/// </summary>
codeunit 56980 "HTTP Content Stream Probe"
{
    /// <summary>
    /// Declares HttpContent + InStream local variables.
    /// BC compiler emits ALLoadFrom(inStream.Value) which after rewriting becomes
    /// ALLoadFrom(MockInStream) — the pattern that caused CS1503 before the fix.
    /// Not invoked by tests; presence here verifies the file compiles without CS1503.
    /// </summary>
    procedure WriteBodyFromStream(var BodyStream: InStream)
    var
        Content: HttpContent;
    begin
        Content.WriteFrom(BodyStream);
    end;

    /// <summary>
    /// Declares HttpContent + InStream local variables.
    /// BC compiler emits ALReadAs(this, DataError, ByRef<NavInStream>) which after
    /// rewriting becomes ByRef<MockInStream> — another CS1503 pattern.
    /// Not invoked by tests; presence here verifies the file compiles.
    /// </summary>
    procedure ReadBodyIntoStream(var DestStream: InStream)
    var
        Content: HttpContent;
    begin
        Content.ReadAs(DestStream);
    end;

    /// <summary>
    /// Pure-logic helper: loads text content and returns it.
    /// Uses ALLoadFrom(NavText) — this always worked; tested to confirm no regression.
    /// </summary>
    procedure WriteBodyFromText(BodyText: Text): Text
    var
        Content: HttpContent;
        Request: HttpRequestMessage;
    begin
        Content.WriteFrom(BodyText);
        Request.Content := Content;
        exit(BodyText);
    end;

    /// <summary>
    /// Pure-logic header check: adds a header and confirms the codeunit compiles.
    /// Uses HttpHeaders.Add(name, value) — already worked; guards against regression.
    /// </summary>
    procedure GetHeaderValue(HeaderName: Text; HeaderValue: Text): Text
    var
        Request: HttpRequestMessage;
        Headers: HttpHeaders;
    begin
        Request.GetHeaders(Headers);
        Headers.Add(HeaderName, HeaderValue);
        exit(HeaderValue);
    end;
}
