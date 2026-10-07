/// <summary>
/// One missing member called two ways: inside an expression, which fixes nothing, and (further down)
/// through an assignment, which fixes its types. The call that fixes nothing sorts first, so a stub
/// shaped from the first call at all would be refused; it is shaped from the first call that fixes it.
/// </summary>
codeunit 65326 "Precompiled Shape Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

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

    [Test]
    procedure MixedShapes_TheCallThatFixesTheTypes()
    var
        Points: Codeunit "Precompiled Points";
        Result: Integer;
    begin
        Result := Points.Shaped(1);
    end;
}
