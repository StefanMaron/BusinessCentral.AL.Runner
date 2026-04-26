codeunit 56401 "Test Database Commit"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Commit_DoesNotError()
    begin
        // Commit() is a no-op in the runner — must not raise any error
        Commit();
        Assert.IsTrue(true, 'Commit() must complete without error');
    end;

    [Test]
    procedure Commit_AfterInsert_RecordStillExists()
    var
        Rec: Record "Commit Test Table";
    begin
        // Insert, commit, then verify the record still exists
        Rec.Id := 1;
        Rec.Value := 'Before Commit';
        Rec.Insert();

        Commit();

        Rec.Reset();
        Assert.AreEqual(1, Rec.Count(), 'Commit() must not clear in-memory record state');
        Rec.Get(1);
        Assert.AreEqual('Before Commit', Rec.Value, 'Committed record must retain its field values');
    end;

    [Test]
    procedure Commit_MultipleTimes_NoError()
    begin
        // Multiple commits in a row must all be no-ops
        Commit();
        Commit();
        Commit();
        Assert.IsTrue(true, 'Multiple Commit() calls must all complete without error');
    end;

    [Test]
    procedure Commit_AfterModify_RecordHasNewValue()
    var
        Rec: Record "Commit Test Table";
    begin
        // Insert + modify + commit — record must retain the modified value
        Rec.Id := 2;
        Rec.Value := 'Original';
        Rec.Insert();

        Rec.Value := 'Modified';
        Rec.Modify();

        Commit();

        Rec.Reset();
        Rec.Get(2);
        Assert.AreEqual('Modified', Rec.Value, 'Commit() after Modify must leave modified value intact');
    end;
}
