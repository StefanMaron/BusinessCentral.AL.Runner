/// <summary>
/// #5044: the argument is a value an enumextension adds to "Loyalty Tier", so --tdd must
/// generate CalcBonus(Arg1: Enum "Loyalty Tier"): Integer.
/// </summary>
codeunit 65116 "Tdd EnumExt Value Arg Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure EnumExtValueArg_GeneratesBaseEnumParameter()
    var
        Target: Codeunit "Tdd Loyalty Cu";
        Result: Integer;
    begin
        Result := Target.CalcBonus("Loyalty Tier"::Platinum);
    end;
}
