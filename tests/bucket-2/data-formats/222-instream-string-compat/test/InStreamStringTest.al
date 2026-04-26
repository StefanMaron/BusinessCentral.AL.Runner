codeunit 62221 "InStream String Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── Compilation probe ────────────────────────────────────────

    [Test]
    procedure XmlDocumentReadFrom_InStream_CompiledSuccessfully()
    var
        Src: Codeunit "InStream String Src";
    begin
        // Positive: codeunit compiled with InStream-form XmlDocument.ReadFrom calls.
        // Sentinel returns 1081 (non-default), proving the codeunit is live.
        Assert.AreEqual(1081, Src.GetVersion(),
            'Codeunit must compile and return 1081 from GetVersion()');
    end;

    [Test]
    procedure XmlDocumentReadFrom_InStream_VersionIsNotZero()
    var
        Src: Codeunit "InStream String Src";
    begin
        // Guards against a no-op stub returning the default integer 0.
        Assert.AreNotEqual(0, Src.GetVersion(),
            'GetVersion must not return 0 — proves codeunit is not a no-op');
    end;
}
