/// <summary>
/// An Option member names no type a parameter could be declared with (an Option parameter
/// needs its member list, which one call site does not fix), so --tdd must refuse it.
/// </summary>
codeunit 65114 "Tdd Option Arg Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure OptionMemberArg_RefusesNotGuesses()
    var
        Target: Codeunit "Tdd Loyalty Cu";
        Choice: Option Alpha,Beta;
        Result: Integer;
    begin
        Result := Target.CalcChoice(Choice::Beta);
    end;
}
