codeunit 50471 "CI Pipeline Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ThreeSequentialAssignments()
    var
        Logic: Codeunit "CI Pipeline Logic";
        X: Integer;
        Y: Integer;
        Z: Integer;
    begin
        // Three sequential assignments — per-statement capture should record
        // each intermediate value in order.
        X := 1;
        Y := 2;
        Z := Logic.Sum(X, Y);

        Assert.AreEqual(3, Z, 'Sum should be 3');
    end;
}
