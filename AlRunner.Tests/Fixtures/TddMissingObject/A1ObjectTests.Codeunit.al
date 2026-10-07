/// <summary>
/// #5431: "No Such Codeunit" is declared by no app. --tdd must add an empty object of that name and the
/// members these calls need, in memory, and a codeunit that DOES exist must run its real body.
/// </summary>
codeunit 65320 "Missing Object Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Shared: Codeunit "No Such Codeunit";

    [Test]
    procedure LocalVariable_RunsToTheStub()
    var
        Missing: Codeunit "No Such Codeunit";
        Result: Integer;
    begin
        Result := Missing.Calc(250);
    end;

    [Test]
    procedure LocalVariable_AssertsItsOwnResult()
    var
        Missing: Codeunit "No Such Codeunit";
        Result: Integer;
    begin
        Result := Missing.Calc(250);
        if Result <> 25 then
            Error('Expected 25, got %1', Result);
    end;

    [Test]
    procedure GlobalVariable_RunsToTheStub()
    var
        Result: Integer;
    begin
        Result := Shared.Bonus(true);
    end;

    [Test]
    procedure DeclaredOnly_NamesTheObject()
    var
        Missing: Codeunit "No Such Codeunit";
    begin
        if Missing.Run() then;
    end;

    [Test]
    procedure ExistingObject_RunsItsRealBody()
    var
        Real: Codeunit "Existing Helper";
    begin
        if Real.Seven() <> 7 then
            Error('the real Seven body did not run');
    end;

    [Test]
    procedure Unrelated_NamesNoStub()
    begin
        if 1 + 1 <> 2 then
            Error('arithmetic');
    end;
}
