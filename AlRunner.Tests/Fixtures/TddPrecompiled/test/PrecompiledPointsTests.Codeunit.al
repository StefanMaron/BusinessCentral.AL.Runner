/// <summary>
/// #5037: "Precompiled Points" arrives as a package (symbols plus a DLL, or symbols plus its source),
/// never as source in this run. --tdd must stub each procedure these tests call that it does not
/// declare, in memory, beside the object, and must not touch the procedures it does declare.
/// </summary>
codeunit 65320 "Precompiled Points Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ExistingMember_RunsTheRealBody()
    var
        Points: Codeunit "Precompiled Points";
    begin
        if Points.Existing() <> 7 then
            Error('the real Existing body did not run');
        if Points.Twice(4) <> 8 then
            Error('the real Twice body did not run');
    end;

    [Test]
    procedure MissingMember_RunsToTheStub()
    var
        Points: Codeunit "Precompiled Points";
        Result: Integer;
    begin
        Result := Points.CalcPoints(250);
    end;

    [Test]
    procedure MissingMember_AssertsItsOwnResult()
    var
        Points: Codeunit "Precompiled Points";
        Result: Integer;
    begin
        Result := Points.CalcPoints(250);
        if Result <> 25 then
            Error('Expected 25, got %1', Result);
    end;

    [Test]
    procedure Unrelated_NamesNoStub()
    var
        Points: Codeunit "Precompiled Points";
    begin
        if Points.Twice(2) <> 4 then
            Error('the real Twice body did not run');
    end;
}
