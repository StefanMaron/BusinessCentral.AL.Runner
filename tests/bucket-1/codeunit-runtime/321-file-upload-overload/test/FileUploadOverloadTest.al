/// Tests for File.Upload 5-param AL form (issue #1531).
///
/// BC AL: File.Upload(DialogTitle, FromFolder, FilterText, FromFile, var ToFile)
/// This is a static method. BC emits in C#:
///   MockFile.ALUpload(scope, DataError, dialogTitle, fromFolder, filterText, fromFile, ByRef NavText toFile)
/// = 7 args. MockFile only had 1- and 2-arg overloads, so CS1501 resulted.
codeunit 1320411 "File Upload Overload Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── File.Upload 5-param static form ─────────────────────────────────────

    /// Positive: Upload with all 5 AL params must compile and run without error.
    /// In standalone mode there is no UI so var ToFile is set to empty.
    [Test]
    procedure Upload_FiveParam_SetsToFileEmpty()
    var
        toFile: Text;
    begin
        toFile := 'original';
        File.Upload('Choose File', 'C:\temp', '*.txt', 'source.txt', toFile);
        // In standalone mode (no UI) the upload is a no-op; ToFile is cleared
        Assert.AreEqual('', toFile, 'File.Upload 5-param: ToFile must be empty string in stub (no UI)');
    end;

    /// Positive: Upload called with empty strings must not throw.
    [Test]
    procedure Upload_FiveParam_NoError()
    var
        toFile: Text;
    begin
        File.Upload('My Dialog', '', 'All Files|*.*', '', toFile);
        Assert.AreEqual('', toFile, 'File.Upload 5-param stub must succeed without error');
    end;
}
