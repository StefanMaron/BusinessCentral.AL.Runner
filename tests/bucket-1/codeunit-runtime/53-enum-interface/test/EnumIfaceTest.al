codeunit 50532 "EI Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure EnumYesDispatchesToTrueImpl()
    var
        Strategy: Enum "EI Flag Strategy";
        Flag: Interface "EI Has Flag";
    begin
        Strategy := Strategy::Yes;
        Flag := Strategy;
        Assert.IsTrue(Flag.GetFlag(), 'Yes strategy should dispatch to the true impl');
    end;

    [Test]
    procedure EnumNoDispatchesToFalseImpl()
    var
        Strategy: Enum "EI Flag Strategy";
        Flag: Interface "EI Has Flag";
    begin
        Strategy := Strategy::No;
        Flag := Strategy;
        Assert.IsFalse(Flag.GetFlag(), 'No strategy should dispatch to the false impl');
    end;
}
