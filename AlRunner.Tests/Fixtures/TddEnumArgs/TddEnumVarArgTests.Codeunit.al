/// <summary>
/// #5038: an enum-typed variable as the argument must generate
/// CalcByTier(Arg1: Enum "Loyalty Tier"): Integer.
/// </summary>
codeunit 65112 "Tdd Enum Var Arg Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure EnumVariableArg_GeneratesEnumParameter()
    var
        Target: Codeunit "Tdd Loyalty Cu";
        Tier: Enum "Loyalty Tier";
        Result: Integer;
    begin
        Tier := Tier::Bronze;
        Result := Target.CalcByTier(Tier);
    end;
}
