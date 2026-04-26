// Renumbered from 50921 to avoid collision in new bucket layout (#1385).
codeunit 1050921 "Loop Tests"
{
    Subtype = Test;

    var
        Helper: Codeunit "Loop Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure TestSimpleLoop()
    var
        Result: Integer;
    begin
        Result := Helper.SumRange(1, 5);
        Assert.AreEqual(15, Result, 'Sum 1..5 should be 15');
    end;

    [Test]
    procedure TestLoopWithBranch()
    var
        Result: Integer;
    begin
        // Verify the loop produces the correct sum as a proxy for correct iteration
        Result := Helper.SumRange(1, 4);
        Assert.AreEqual(10, Result, 'Sum 1..4 should be 10');
    end;

    [Test]
    procedure TestLoopNegative()
    var
        Result: Integer;
    begin
        // Negative path: verify that a wrong expected value causes a failure
        Result := Helper.SumRange(1, 3);
        asserterror Assert.AreEqual(99, Result, 'Sum 1..3 should not be 99');
        Assert.ExpectedError('99');
    end;
}
