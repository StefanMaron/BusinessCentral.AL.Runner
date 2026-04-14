/// <summary>
/// Probe codeunit for test 96 — NavHttpContent.ALLoadFrom/ALReadAs with InStream.
/// When AL code calls HttpContent.WriteFrom(InStream) or HttpContent.ReadAs(var InStream),
/// the BC compiler generates ALLoadFrom(MockInStream) / ALReadAs(this, DataError, ByRef<MockInStream>)
/// after the NavInStream->MockInStream type rename in the rewriter.
/// NavHttpContent.ALLoadFrom expects NavInStream, not MockInStream -> CS1503.
/// The fix: rewriter redirects these calls to AlCompat helpers that accept MockInStream.
///
/// WriteBodyFromStream() and ReadBodyIntoStream() are compilation-proof methods: they
/// declare the problematic InStream patterns so that their mere presence proves the
/// CS1503 fix works.  They are NEVER called by tests (HTTP requires a BC service tier).
///
/// GetProbeVersion() is a pure-logic sentinel with no HTTP variables.  Tests call it
/// to confirm the codeunit was successfully loaded into the in-memory assembly —
/// proof that the CS1503 error is gone and the file compiled without exclusion.
/// </summary>
codeunit 56980 "HTTP Content Stream Probe"
{
    /// <summary>
    /// Proof-of-compilation: HttpContent.WriteFrom(InStream).
    /// BC emits ALLoadFrom(inStream.Value); after NavInStream->MockInStream rename
    /// this becomes ALLoadFrom(MockInStream) which caused CS1503 before the fix.
    /// Never called by tests — HTTP is not available in standalone mode.
    /// </summary>
    procedure WriteBodyFromStream(var BodyStream: InStream)
    var
        Content: HttpContent;
    begin
        Content.WriteFrom(BodyStream);
    end;

    /// <summary>
    /// Proof-of-compilation: HttpContent.ReadAs(var InStream).
    /// BC emits ALReadAs(this, DataError.ThrowError, ByRef-NavInStream-); after rename
    /// this becomes ByRef-MockInStream- which caused CS1503 before the fix.
    /// Never called by tests — HTTP is not available in standalone mode.
    /// </summary>
    procedure ReadBodyIntoStream(var DestStream: InStream)
    var
        Content: HttpContent;
    begin
        Content.ReadAs(DestStream);
    end;

    /// <summary>
    /// Pure-logic sentinel: no HTTP variables, no Nav* constructors.
    /// Tests call this to confirm the codeunit is loaded in the assembly,
    /// which proves compilation succeeded (CS1503 is gone).
    /// </summary>
    procedure GetProbeVersion(): Integer
    begin
        exit(96);
    end;

    /// <summary>
    /// Pure-logic helper: returns a formatted request description without
    /// actually creating any NavHttp* objects.  Guards against regression
    /// in pure-text logic while staying safe from Parent.Tree checks.
    /// </summary>
    procedure FormatRequestLine(Method: Text; Url: Text): Text
    begin
        exit(Method + ' ' + Url);
    end;
}
