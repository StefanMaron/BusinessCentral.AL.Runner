codeunit 52800 "Test Ext Fields"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure SetAndReadExtensionField()
    var
        BaseRec: Record "Base Table";
    begin
        // Positive: extension field can be written and read back
        BaseRec.Init();
        BaseRec."No." := 'T001';
        BaseRec."Custom Amount" := 42.5;
        BaseRec."Custom Code" := 'ABC';
        BaseRec.Insert(true);

        BaseRec.Get('T001');
        Assert.AreEqual(42.5, BaseRec."Custom Amount", 'Custom Amount should be 42.5');
        Assert.AreEqual('ABC', BaseRec."Custom Code", 'Custom Code should be ABC');
    end;

    [Test]
    procedure ExtensionFieldDefaultsToZero()
    var
        BaseRec: Record "Base Table";
    begin
        // Positive: unset extension field defaults to type default
        BaseRec.Init();
        BaseRec."No." := 'T002';
        BaseRec.Insert(true);

        BaseRec.Get('T002');
        Assert.AreEqual(0, BaseRec."Custom Amount", 'Custom Amount should default to 0');
        Assert.AreEqual('', BaseRec."Custom Code", 'Custom Code should default to empty');
    end;

    [Test]
    procedure CrossCodeunitExtensionFieldAccess()
    var
        BaseRec: Record "Base Table";
        Logic: Codeunit "Ext Field Logic";
        Result: Decimal;
    begin
        // Positive: extension fields work across codeunit calls
        BaseRec.Init();
        BaseRec."No." := 'T003';
        Logic.SetCustomFields(BaseRec, 25.0, 'XYZ');
        BaseRec.Insert(true);

        BaseRec.Get('T003');
        Result := Logic.DoubleCustomAmount(BaseRec);
        Assert.AreEqual(50.0, Result, 'Double of 25.0 should be 50.0');
    end;

    [Test]
    procedure ExtensionFieldWrongValue()
    var
        BaseRec: Record "Base Table";
    begin
        // Negative: verify extension field holds actual value, not default
        BaseRec.Init();
        BaseRec."No." := 'T004';
        BaseRec."Custom Amount" := 99.9;
        BaseRec.Insert(true);

        BaseRec.Get('T004');
        Assert.AreNotEqual(0, BaseRec."Custom Amount", 'Custom Amount should not be 0');
        Assert.AreNotEqual(100.0, BaseRec."Custom Amount", 'Custom Amount should not be 100');
    end;
}
