/// <summary>
/// A second file calling the same missing member (its own stub, not a shared one) and a second
/// missing member of the same object, through a global variable.
/// </summary>
codeunit 65321 "Precompiled Points More Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        GlobalPoints: Codeunit "Precompiled Points";

    [Test]
    procedure SameMember_InASecondFile()
    var
        Result: Integer;
    begin
        Result := GlobalPoints.CalcPoints(5);
    end;

    [Test]
    procedure SecondMember_ThroughAGlobalVariable()
    var
        Result: Integer;
    begin
        Result := GlobalPoints.Bonus(true);
        if GlobalPoints.Twice(3) <> 6 then
            Error('the real Twice body did not run');
    end;
}
