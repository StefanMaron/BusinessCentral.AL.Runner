/// <summary>"Pick" is called here as a Boolean, in A2 as an Integer. A2 sorts first, so the Integer stub wins and
/// this assignment is what no longer compiles: the file is FAILED, whichever order the compile reported the calls in.</summary>
codeunit 65323 "Missing Object Disagree Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure DisagreeingShapes_TheSecondFileLoses()
    var
        Missing: Codeunit "No Such Codeunit";
        Result: Boolean;
    begin
        Result := Missing.Pick(1);
    end;
}
