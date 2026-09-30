/// <summary>
/// #5038: an integer literal next to an enum value must generate
/// CalcPoints(Arg1: Integer; Arg2: Enum "Loyalty Tier"): Integer.
/// </summary>
codeunit 65111 "Tdd Mixed Arg Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure LiteralAndEnumValueArgs_GenerateBothParameters()
    var
        Target: Codeunit "Tdd Loyalty Cu";
        Result: Integer;
    begin
        Result := Target.CalcPoints(250, "Loyalty Tier"::Gold);
    end;
}
