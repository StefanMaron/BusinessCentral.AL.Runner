/// <summary>
/// #5228 across two source folders: Existing(A) lives in the app folder and the test calls it with
/// one more argument, so the overload is generated into the app bundle, in memory.
/// </summary>
codeunit 65320 "Overload Two Folder Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ExtraArgument_RunsAgainstGeneratedOverload()
    var
        Calc: Codeunit "Overload Calc";
        Result: Integer;
    begin
        Result := Calc.Existing(5, 7);
        if Result <> 0 then
            Error('an empty generated overload returns 0, got %1', Result);
    end;

    [Test]
    procedure OwnArgument_StillRunsTheExistingProcedure()
    var
        Calc: Codeunit "Overload Calc";
    begin
        if Calc.Existing(5) <> 5 then
            Error('the existing procedure returns its argument');
    end;
}
