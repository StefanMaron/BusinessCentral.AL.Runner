codeunit 50121 "FieldRef SetTable Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestSetTableCompiles()
    var
        Helper: Codeunit "FieldRef SetTable Helper";
    begin
        Assert.IsTrue(Helper.TestFieldRefSetTable(), 'SetTable should work');
    end;

    [Test]
    procedure TestSetTableNegative()
    var
        Helper: Codeunit "FieldRef SetTable Helper";
    begin
        // Negative: calling the helper on an empty table should still succeed
        Assert.IsTrue(Helper.TestFieldRefSetTable(), 'SetTable on empty table should not error');
    end;
}
