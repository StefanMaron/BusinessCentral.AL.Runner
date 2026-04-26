codeunit 1269002 "DTC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "DTC Src";

    [Test]
    procedure Clear_AfterSetup_DoesNotThrow()
    begin
        // Positive: calling Clear after SetTables + AddFieldValue + AddConstantValue
        // must not throw -- the method must exist and be callable.
        Src.ClearAfterSetup_DoesNotThrow();
        Assert.IsTrue(true, 'DataTransfer.Clear must not throw');
    end;

    [Test]
    procedure Clear_ThenCopyFields_DoesNotThrow()
    begin
        // Positive: Clear followed by CopyFields must not throw.
        Src.ClearThenCopyFields_DoesNotThrow();
        Assert.IsTrue(true, 'CopyFields after Clear must not throw');
    end;

    [Test]
    procedure Clear_ThenCopyRows_DoesNotThrow()
    begin
        // Positive: Clear followed by CopyRows must not throw.
        Src.ClearThenCopyRows_DoesNotThrow();
        Assert.IsTrue(true, 'CopyRows after Clear must not throw');
    end;

    [Test]
    procedure Clear_ResetsUpdateAuditFields()
    begin
        // Positive: after Clear, UpdateAuditFields must be back to default (false).
        Assert.IsFalse(Src.UpdateAuditFieldsSurvivesClear(),
            'UpdateAuditFields must be false after Clear');
    end;
}
