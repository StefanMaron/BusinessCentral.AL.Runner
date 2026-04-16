codeunit 59561 "DTA Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "DTA Src";

    [Test]
    procedure UpdateAuditFields_DefaultFalse()
    begin
        // Positive: BC's DataTransfer.UpdateAuditFields default is false.
        Assert.IsFalse(Src.GetUpdateAuditFieldsDefault(),
            'DataTransfer.UpdateAuditFields must default to false');
    end;

    [Test]
    procedure UpdateAuditFields_SetTrueReadBack()
    begin
        // Positive: setter must take effect; reader must round-trip.
        Assert.IsTrue(Src.SetAndGetUpdateAuditFields(true),
            'UpdateAuditFields := true must read back as true');
    end;

    [Test]
    procedure UpdateAuditFields_SetFalseReadBack()
    begin
        Assert.IsFalse(Src.SetAndGetUpdateAuditFields(false),
            'UpdateAuditFields := false must read back as false');
    end;

    [Test]
    procedure CopyFieldsWithAudit_DoesNotThrow()
    begin
        // Negative trap: the entire flow (SetTables + AddFieldValue +
        // UpdateAuditFields := true + CopyFields) must complete without throwing.
        Src.CopyFieldsWithAudit();
        Assert.IsTrue(true, 'CopyFields with UpdateAuditFields must not throw');
    end;
}
