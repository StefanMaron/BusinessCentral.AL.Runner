codeunit 50154 "DED Database ExportData Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "DED Helper";

    [Test]
    procedure ExportData_WithFilename_DoesNotThrow()
    begin
        // Positive: Database.ExportData(FileName) must compile and execute without
        // throwing an error. The runner stubs it as a no-op (file I/O out of scope).
        Helper.CallExportData('test.dat');
        Assert.IsTrue(true, 'ExportData must not throw');
    end;

    [Test]
    procedure ExportData_WithEmptyFilename_DoesNotThrow()
    begin
        // Positive: empty filename must also be accepted without error.
        Helper.CallExportData('');
        Assert.IsTrue(true, 'ExportData with empty filename must not throw');
    end;

    [Test]
    procedure ExportData_CalledMultipleTimes_DoesNotThrow()
    begin
        // Positive: multiple calls must not accumulate errors.
        Helper.CallExportData('first.dat');
        Helper.CallExportData('second.dat');
        Assert.IsTrue(true, 'Repeated ExportData calls must not throw');
    end;

    [Test]
    procedure HelperCodeunit_Add_ProvesBusiness Logic()
    begin
        // Positive: helper codeunit alongside ExportData call is fully functional.
        Assert.AreEqual(7, Helper.Add(3, 4), 'Add(3,4) must return 7');
        Assert.AreEqual(0, Helper.Add(-1, 1), 'Add(-1,1) must return 0');
    end;

    [Test]
    procedure HelperCodeunit_Add_NotWrongResult()
    begin
        // Negative: guard against no-op mock returning default value.
        Assert.AreNotEqual(0, Helper.Add(3, 4), 'Add(3,4) must not return 0');
    end;
}
