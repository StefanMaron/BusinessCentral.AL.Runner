codeunit 58401 "Test Database SelectLatestVersion"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure SelectLatestVersion_DoesNotError()
    begin
        // SelectLatestVersion() is a no-op in the runner — must not raise any error
        SelectLatestVersion();
        Assert.IsTrue(true, 'SelectLatestVersion() must complete without error');
    end;

    [Test]
    procedure SelectLatestVersion_AfterInsert_RecordStillVisible()
    var
        Rec: Record "SelectLatestVersion Test Table";
    begin
        // Insert a record, call SelectLatestVersion(), then verify the record is still visible
        Rec.Id := 1;
        Rec.Value := 'Hello';
        Rec.Insert();

        SelectLatestVersion();

        Rec.Reset();
        Assert.AreEqual(1, Rec.Count(), 'SelectLatestVersion() must not clear in-memory record state');
        Rec.Get(1);
        Assert.AreEqual('Hello', Rec.Value, 'Record field must retain its value after SelectLatestVersion()');
    end;

    [Test]
    procedure SelectLatestVersion_MultipleTimes_NoError()
    begin
        // Multiple calls must all be no-ops
        SelectLatestVersion();
        SelectLatestVersion();
        SelectLatestVersion();
        Assert.IsTrue(true, 'Multiple SelectLatestVersion() calls must all complete without error');
    end;
}
