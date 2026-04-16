codeunit 87001 "FIO Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "FIO Src";

    // -----------------------------------------------------------------------
    // Positive: File.Exists always returns false in standalone mode —
    // no real filesystem is accessible.
    // -----------------------------------------------------------------------

    [Test]
    procedure FileExists_AnyPath_ReturnsFalse()
    begin
        // Positive: no file exists in standalone mode.
        Assert.IsFalse(Src.FileExists('C:\does-not-exist.txt'),
            'File.Exists must return false in standalone mode');
    end;

    [Test]
    procedure FileExists_EmptyPath_ReturnsFalse()
    begin
        // Positive: empty path also returns false — not an error.
        Assert.IsFalse(Src.FileExists(''),
            'File.Exists with empty path must return false');
    end;

    // -----------------------------------------------------------------------
    // Positive: File.Write + File.Read must round-trip content correctly.
    // -----------------------------------------------------------------------

    [Test]
    procedure WriteRead_RoundTrips_Content()
    var
        Content: Text;
        Result: Text;
    begin
        // Positive: content written must be readable back unchanged.
        Content := 'Hello standalone file';
        Result := Src.WriteAndReadBack(Content);
        Assert.AreEqual(Content, Result,
            'File.Read must return the exact string written by File.Write');
    end;

    [Test]
    procedure WriteRead_EmptyContent_RoundTrips()
    var
        Result: Text;
    begin
        // Positive: empty string must also round-trip without error.
        Result := Src.WriteAndReadBack('');
        Assert.AreEqual('', Result,
            'File.Read on empty write must return empty string');
    end;

    // -----------------------------------------------------------------------
    // Negative: File.Len after writing returns a positive length.
    // -----------------------------------------------------------------------

    [Test]
    procedure Len_AfterWrite_IsPositive()
    var
        Len: Integer;
    begin
        // Negative: after writing content, Len must be > 0 (not default 0).
        Len := Src.CreateAndLen('test data');
        Assert.IsTrue(Len > 0,
            'File.Len after Write must be greater than zero');
    end;

    [Test]
    procedure Len_AfterEmptyWrite_IsZero()
    var
        Len: Integer;
    begin
        // Negative: after writing empty content, Len must be 0.
        Len := Src.CreateAndLen('');
        Assert.AreEqual(0, Len,
            'File.Len after writing empty string must be 0');
    end;
}
