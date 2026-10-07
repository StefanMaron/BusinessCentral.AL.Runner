/// <summary>
/// A procedure the object DOES declare, called with an argument count none of its overloads takes. A
/// source object gets an overload generated beside it; for a precompiled one --tdd refuses (#5037), so
/// the test is reported FAILED rather than run against a stub it could not have inferred the need for.
/// </summary>
codeunit 65324 "Precompiled Overload Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure NewOverloadOfAnExistingProcedure_IsRefused()
    var
        Points: Codeunit "Precompiled Points";
        Result: Integer;
    begin
        Result := Points.Twice(1, 2);
    end;
}
