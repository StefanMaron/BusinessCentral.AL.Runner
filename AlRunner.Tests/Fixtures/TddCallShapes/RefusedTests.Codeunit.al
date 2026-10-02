/// <summary>
/// Calls the generator cannot anchor and must refuse (#5146, #5228): a Text expected value (a
/// length nothing fixes), a sibling that is itself missing, Variant siblings that disagree, and a
/// bare-statement call to an existing procedure with an extra argument (void or discarded?).
/// Each test is reported failed, naming the missing symbol.
/// </summary>
codeunit 65203 "Tdd Shape Refused Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tdd Shape Assert";

    [Test]
    procedure NestedTextExpected_Refuses()
    var
        Target: Codeunit "Tdd Shape Target Cu";
    begin
        Assert.AreEqual('Gold', Target.TierName(250), 'TierName(250) should be Gold');
    end;

    [Test]
    procedure BothSidesMissing_Refuses()
    var
        Target: Codeunit "Tdd Shape Target Cu";
    begin
        Assert.AreEqual(Target.ExpectedOf(1), Target.ActualOf(1), 'both sides missing');
    end;

    [Test]
    procedure VariantSiblingsDisagree_Refuses()
    var
        Target: Codeunit "Tdd Shape Target Cu";
    begin
        Assert.Between(1, Target.ValueOf(1), 2.5, 'Integer low, Decimal high');
    end;

    [Test]
    procedure BareStatementWithExtraArgument_Refuses()
    var
        Target: Codeunit "Tdd Shape Target Cu";
    begin
        Target.Existing(5, 7, 9);
    end;
}
