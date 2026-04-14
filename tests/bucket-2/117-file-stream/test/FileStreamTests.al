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
    procedure TestUploadDoesNotError()
    var
        Helper: Codeunit "File Stream Helper";
        Result: Boolean;
    begin
        // Positive: calling UploadIntoStream doesn't throw
        Result := Helper.TestUploadWithFileName();
        Assert.IsTrue(true, 'UploadIntoStream should not throw');
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
    procedure TestDownloadDoesNotError()
    var
        Helper: Codeunit "File Stream Helper";
        Result: Boolean;
    begin
        // Positive: calling DownloadFromStream doesn't throw
        Result := Helper.TestDownloadFromStream();
        Assert.IsTrue(true, 'DownloadFromStream should not throw');
    end;
}
