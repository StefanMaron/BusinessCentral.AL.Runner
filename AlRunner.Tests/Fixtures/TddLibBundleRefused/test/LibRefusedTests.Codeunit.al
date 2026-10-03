/// <summary>
/// #5243: reaches a member --tdd refuses to generate, only through the library bundle. The
/// library cannot compile, so the call fails when it is made. The second test does not touch it.
/// </summary>
codeunit 65490 "Lib Refused Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ViaLibrary_FailsBecauseTheLibraryCannotCompile()
    var
        Helper: Codeunit "Lib Refused Helper";
    begin
        if Helper.Calc() <> 0 then
            Error('unreachable');
    end;

    [Test]
    procedure NotTouchingTheLibrary_StillRuns()
    begin
        if 1 + 1 <> 2 then
            Error('arithmetic');
    end;
}
