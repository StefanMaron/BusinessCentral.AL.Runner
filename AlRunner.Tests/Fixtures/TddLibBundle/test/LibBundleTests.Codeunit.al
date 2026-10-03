/// <summary>
/// #5243, #5161: reaches "Lib Bundle Loyalty.Missing" only through the library bundles. The stub
/// returns the default, so each assertion holds and the tests pass against it.
/// </summary>
codeunit 65460 "Lib Bundle Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ViaLibrary_RunsAgainstTheGeneratedStub()
    var
        Helper: Codeunit "Lib Bundle Helper";
    begin
        if Helper.Calc() <> 0 then
            Error('the stub returns the default');
    end;

    [Test]
    procedure ViaTwoLibraries_RunsAgainstTheGeneratedStub()
    var
        Wrapper: Codeunit "Lib Bundle Wrapper";
    begin
        if Wrapper.Wrap() <> 0 then
            Error('the stub returns the default');
    end;

    [Test]
    procedure LibraryProcedureThatReachesNothingMissing_IsNotAnnotated()
    var
        Helper: Codeunit "Lib Bundle Helper";
    begin
        if Helper.Other() <> 7 then
            Error('Other returns 7');
    end;
}
