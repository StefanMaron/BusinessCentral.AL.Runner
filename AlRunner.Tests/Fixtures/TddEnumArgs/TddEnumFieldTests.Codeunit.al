/// <summary>
/// #5038, the field sibling: the missing field's type comes from the same resolver as a
/// procedure argument, so an enum value on the right-hand side must generate
/// "Tier": Enum "Loyalty Tier".
/// </summary>
codeunit 65115 "Tdd Enum Field Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure EnumValueAssignment_GeneratesEnumField()
    var
        Member: Record "Tdd Loyalty Member";
    begin
        Member."Tier" := "Loyalty Tier"::Gold;
        if Member."Tier" <> "Loyalty Tier"::Gold then
            Error('the generated field did not hold the enum value');
    end;
}
