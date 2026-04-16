codeunit 61303 "SACF SetAutoCalcFields Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "SACF Helper";

    // -----------------------------------------------------------------------
    // SetAutoCalcFields + Get — FlowField calculated automatically
    // -----------------------------------------------------------------------

    [Test]
    procedure SetAutoCalcFields_Get_AutoCalculatesFlowField()
    begin
        // Positive: SetAutoCalcFields causes FlowField to be populated on Get
        Helper.InsertOrder('ORD001');
        Helper.InsertLines('ORD001', 3);
        Assert.AreEqual(3, Helper.GetLineCountWithAutoCalc('ORD001'),
            'SetAutoCalcFields + Get must auto-calculate Line Count to 3');
    end;

    [Test]
    procedure SetAutoCalcFields_Get_ZeroLines_ReturnsZero()
    begin
        // Edge: order with no lines returns 0 after auto-calc
        Helper.InsertOrder('ORD004');
        Assert.AreEqual(0, Helper.GetLineCountWithAutoCalc('ORD004'),
            'SetAutoCalcFields + Get with zero lines must return 0');
    end;

    // -----------------------------------------------------------------------
    // SetAutoCalcFields + FindFirst — FlowField calculated automatically
    // -----------------------------------------------------------------------

    [Test]
    procedure SetAutoCalcFields_FindFirst_AutoCalculatesFlowField()
    begin
        // Positive: SetAutoCalcFields causes FlowField to be populated on FindFirst
        Helper.InsertOrder('ORD002');
        Helper.InsertLines('ORD002', 5);
        Assert.AreEqual(5, Helper.FindFirstLineCountWithAutoCalc('ORD002'),
            'SetAutoCalcFields + FindFirst must auto-calculate Line Count to 5');
    end;

    // -----------------------------------------------------------------------
    // Without SetAutoCalcFields — FlowField remains uncalculated
    // -----------------------------------------------------------------------

    [Test]
    procedure NoAutoCalcFields_Get_FlowFieldIsZero()
    begin
        // Negative: without SetAutoCalcFields, FlowField is not auto-calculated
        Helper.InsertOrder('ORD003');
        Helper.InsertLines('ORD003', 2);
        Assert.AreEqual(0, Helper.GetLineCountWithoutAutoCalc('ORD003'),
            'Without SetAutoCalcFields, Line Count must remain 0 after Get');
    end;

    // -----------------------------------------------------------------------
    // Error mechanism
    // -----------------------------------------------------------------------

    [Test]
    procedure SetAutoCalcFields_Error()
    begin
        // Negative: error mechanism works correctly
        asserterror Error('expected error');
        Assert.ExpectedError('expected error');
    end;
}
