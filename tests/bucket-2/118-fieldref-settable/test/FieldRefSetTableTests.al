codeunit 50121 "FieldRef SetTable Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestSetTableCopiesEntryNo()
    var
        Helper: Codeunit "FieldRef SetTable Helper";
        EntryNo: Integer;
        Desc: Text[100];
    begin
        Helper.SetTableCopiesData(EntryNo, Desc);
        Assert.AreEqual(42, EntryNo, 'SetTable should copy Entry No. from RecRef');
    end;

    [Test]
    procedure TestSetTableCopiesDescription()
    var
        Helper: Codeunit "FieldRef SetTable Helper";
        EntryNo: Integer;
        Desc: Text[100];
    begin
        Helper.SetTableCopiesData(EntryNo, Desc);
        Assert.AreEqual('SetTableTest', Desc, 'SetTable should copy Description from RecRef');
    end;
}
