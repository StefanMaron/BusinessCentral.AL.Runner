/// <summary>
/// One missing member called two ways: through an assignment, which fixes its types, and inside an
/// expression, which fixes nothing. The stub takes its shape from the call that fixes it, whichever
/// of the two the compiler reports first (the order changes from run to run).
/// </summary>
codeunit 65326 "Precompiled Shape Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MixedShapes_TheCallThatFixesTheTypes()
    var
        Points: Codeunit "Precompiled Points";
        Result: Integer;
    begin
        Result := Points.Shaped(1);
    end;

    [Test]
    procedure MixedShapes_TheCallInsideAnExpression()
    var
        Points: Codeunit "Precompiled Points";
        Total: Integer;
    begin
        Total := Points.Shaped(9) + Points.Twice(1);
        if Total <> 2 then
            Error('Expected the stub to answer 0 and Twice(1) to answer 2, got %1', Total);
    end;
}
