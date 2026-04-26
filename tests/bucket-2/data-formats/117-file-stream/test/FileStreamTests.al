codeunit 50119 "File Stream Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestUploadReturnsFalse()
    var
        Helper: Codeunit "File Stream Helper";
    begin
        // UploadIntoStream returns false in standalone mode (no client)
        Assert.IsFalse(Helper.TestUploadWithFileName(), 'Upload should return false');
    end;

    [Test]
    procedure TestUploadResultIsFalse()
    var
        Helper: Codeunit "File Stream Helper";
        Result: Boolean;
    begin
        // Verify the return value is consistently false
        Result := Helper.TestUploadWithFileName();
        Assert.AreEqual(false, Result, 'Upload result should be false');
    end;

    [Test]
    procedure TestDownloadReturnsFalse()
    var
        Helper: Codeunit "File Stream Helper";
    begin
        // DownloadFromStream returns false in standalone mode (no client)
        Assert.IsFalse(Helper.TestDownloadFromStream(), 'Download should return false');
    end;

    [Test]
    procedure TestDownloadResultIsFalse()
    var
        Helper: Codeunit "File Stream Helper";
        Result: Boolean;
    begin
        // Verify the return value is consistently false
        Result := Helper.TestDownloadFromStream();
        Assert.AreEqual(false, Result, 'Download result should be false');
    end;
}
