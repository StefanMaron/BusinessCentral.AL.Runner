/// <summary>
/// The library has two objects, so the dependency compile against the app's old symbols is PARTIAL.
/// "ThroughLocalHelper" reaches the library through a procedure of its own codeunit, not in its body.
/// </summary>
codeunit 65570 "Lib Overload Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ViaLibrary_RunsAgainstTheGeneratedStub()
    var
        Helper: Codeunit "Lib Overload Helper";
    begin
        if Helper.Calc() <> 0 then
            Error('the stub returns the default');
    end;

    [Test]
    procedure ThroughLocalHelper_RunsAgainstTheGeneratedStub()
    begin
        Go();
    end;

    [Test]
    procedure OtherLibraryObject_IsNotAnnotated()
    var
        Other: Codeunit "Lib Overload Other";
    begin
        if Other.Seven() <> 7 then
            Error('Seven returns 7');
    end;

    local procedure Go()
    var
        Helper: Codeunit "Lib Overload Helper";
    begin
        if Helper.Calc() <> 0 then
            Error('the stub returns the default');
    end;
}
