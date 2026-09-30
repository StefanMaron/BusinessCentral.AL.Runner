/// <summary>
/// #5037: every procedure these tests call is missing from "Loyalty Points", which lives in
/// the sibling app folder. --tdd must generate each one into that app, in memory.
/// </summary>
codeunit 65220 "Two Folder Loyalty Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure VariableArg_GeneratesDecimalParameter()
    var
        LoyaltyPoints: Codeunit "Loyalty Points";
        Amount: Decimal;
        Result: Integer;
    begin
        Amount := 12.5;
        Result := LoyaltyPoints.CalcBasePoints(Amount);
    end;

    [Test]
    procedure LiteralArg_GeneratesIntegerParameter()
    var
        LoyaltyPoints: Codeunit "Loyalty Points";
        Result: Integer;
    begin
        Result := LoyaltyPoints.CalcLit(250);
    end;

    [Test]
    procedure EnumValueArg_GeneratesEnumParameter()
    var
        LoyaltyPoints: Codeunit "Loyalty Points";
        Result: Integer;
    begin
        Result := LoyaltyPoints.CalcTier("Two Folder Tier"::Gold);
    end;
}
