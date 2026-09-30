/// <summary>
/// #5038: CalcTier is missing from "Tdd Loyalty Cu"; its only argument is an enum value, so
/// --tdd must generate CalcTier(Arg1: Enum "Loyalty Tier"): Integer.
/// </summary>
codeunit 65110 "Tdd Enum Value Arg Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure EnumValueArg_GeneratesEnumParameter()
    var
        Target: Codeunit "Tdd Loyalty Cu";
        Result: Integer;
    begin
        Result := Target.CalcTier("Loyalty Tier"::Gold);
    end;
}
