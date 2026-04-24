/// Tests for UploadIntoStream with a local InStream (issues #1213/#1214).
/// BC emits `ALUploadIntoStream(DataError, string, ByRef<MockInStream>, Guid)`
/// — previously triggered CS1503 at args 1 (DataError→string) and 4
/// (Guid→MockInStream) because C# overload resolution fell back to the
/// 4-arg `(string, string, ByRef<NavText>, MockInStream)` signature.
codeunit 121212 "UIS Local Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Positive: The local-InStream form compiles and returns false in
    // standalone mode (no client/UI). Proves our rewriter + MockFile overload
    // resolve the BC-emitted call shape for issues #1213/#1214.
    // -----------------------------------------------------------------------

    [Test]
    procedure UploadIntoStreamLocal_ReturnsFalse_NoThrow()
    var
        Src: Codeunit "UIS Local Src";
    begin
        Assert.IsFalse(Src.CallUploadIntoStreamLocal(),
            'UploadIntoStream with local InStream must return false in standalone mode (no client)');
    end;

    // -----------------------------------------------------------------------
    // Negative: A broken stub that returns true would be caught here.
    // Asserting true must fail and produce the expected error.
    // -----------------------------------------------------------------------

    [Test]
    procedure UploadIntoStreamLocal_AssertingTrueFails()
    var
        Src: Codeunit "UIS Local Src";
    begin
        asserterror Assert.IsTrue(Src.CallUploadIntoStreamLocal(),
            'This assertion should fail because the stub returns false.');
        Assert.ExpectedError('This assertion should fail');
    end;
}
