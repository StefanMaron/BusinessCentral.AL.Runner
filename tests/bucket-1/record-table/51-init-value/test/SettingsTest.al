codeunit 50511 "IV Settings Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure InitAppliesEnumInitValue()
    var
        S: Record "IV Settings";
    begin
        S.Init();
        Assert.AreEqual(S.Mode::Daily, S.Mode, 'Init should apply Mode=Daily');
    end;

    [Test]
    procedure InitAppliesIntegerInitValue()
    var
        S: Record "IV Settings";
    begin
        S.Init();
        Assert.AreEqual(30, S."Retention Days", 'Init should apply Retention Days=30');
    end;

    [Test]
    procedure InitAppliesBooleanInitValue()
    var
        S: Record "IV Settings";
    begin
        S.Init();
        Assert.IsTrue(S.Enabled, 'Init should apply Enabled=true');
    end;

    [Test]
    procedure InitValueLeavesUnspecifiedFieldsZero()
    var
        S: Record "IV Settings";
    begin
        S.Init();
        Assert.AreEqual(0, S.Id, 'Id has no InitValue, must be 0');
    end;
}
