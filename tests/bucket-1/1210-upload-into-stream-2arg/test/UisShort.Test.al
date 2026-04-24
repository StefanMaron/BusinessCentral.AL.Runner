/// Tests for the 2-arg AL form UploadIntoStream(Title, var InStream).
/// Issue #1210 — rewriter/signature gap caused CS1503 at compile time.
codeunit 121012 "UIS2 Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Positive: The 2-arg AL form compiles and returns false in standalone
    // mode (no client/UI is available). This proves the rewriter produces
    // a call site that matches a MockFile overload.
    // -----------------------------------------------------------------------

    [Test]
    procedure UploadIntoStream2Arg_ReturnsFalse_NoThrow()
    var
        Src: Codeunit "UIS2 Src";
        InStr: InStream;
    begin
        Assert.IsFalse(Src.CallUploadIntoStream2Arg(InStr),
            '2-arg UploadIntoStream must return false in standalone mode (no client)');
    end;

    // -----------------------------------------------------------------------
    // Negative: A stub that always "succeeds" would incorrectly return true.
    // Ensure asserting true fails — this catches a broken mock that flips
    // the return value.
    // -----------------------------------------------------------------------

    [Test]
    procedure UploadIntoStream2Arg_AssertingTrueFails()
    var
        Src: Codeunit "UIS2 Src";
        InStr: InStream;
    begin
        asserterror Assert.IsTrue(Src.CallUploadIntoStream2Arg(InStr),
            'This assertion should fail because the stub returns false.');
        Assert.ExpectedError('This assertion should fail');
    end;
}
