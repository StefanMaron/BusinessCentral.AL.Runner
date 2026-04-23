/// Tests for UploadIntoStream stub behaviour (issue #1021).
/// The 5-arg AL form (Title, Folder, Filter, FileName, InStream) is tested here.
/// The 4-arg AL form (Title, Filter, FileName, InStream) — available in newer BC
/// versions — is covered at the C# level in AlRunner.Tests/MockFile4ArgTests.cs.
codeunit 160003 "UIS Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Positive: UploadIntoStream returns false in standalone mode.
    // No client/UI is available, so the stub must always return false.
    // -----------------------------------------------------------------------

    [Test]
    procedure UploadIntoStream_ReturnsFalse_NoThrow()
    var
        Src: Codeunit "UIS Src";
        InStr: InStream;
        FileName: Text;
    begin
        // The entire claim: compiles and returns false (no crash).
        Assert.IsFalse(Src.CallUploadIntoStream(FileName, InStr),
            'UploadIntoStream must return false in standalone mode (no client)');
    end;

    // -----------------------------------------------------------------------
    // Positive: FileName is empty after the call (stub clears it).
    // This proves the stub actively touches FileName, not just that it runs.
    // -----------------------------------------------------------------------

    [Test]
    procedure UploadIntoStream_ClearsFileName()
    var
        Src: Codeunit "UIS Src";
        InStr: InStream;
        FileName: Text;
    begin
        FileName := 'preset-value';
        Src.CallUploadIntoStream(FileName, InStr);
        Assert.AreEqual('', FileName,
            'UploadIntoStream stub must clear FileName (no upload in standalone mode)');
    end;
}
