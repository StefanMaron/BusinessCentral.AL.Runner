codeunit 84408 "Media Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Media Src";

    [Test]
    procedure ImportFile_ReturnsTrue()
    begin
        Assert.IsTrue(Src.ImportFileReturnsTrue('image.jpg'),
            'ImportFile must return true (in-memory stub)');
    end;

    [Test]
    procedure ExportFile_DefaultMedia_ReturnsFalse()
    begin
        Assert.IsFalse(Src.ExportFileOnDefaultReturnsFalse('output.jpg'),
            'ExportFile on default Media must return false (no data to export)');
    end;

}
