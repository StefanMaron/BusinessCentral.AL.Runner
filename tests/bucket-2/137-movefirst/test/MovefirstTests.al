codeunit 60201 "MF Movefirst Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "MF Helper";

    // -----------------------------------------------------------------------
    // Positive: pageextension with movefirst compiles; logic runs
    // -----------------------------------------------------------------------

    [Test]
    procedure Movefirst_PageExtCompiles_LogicRuns()
    begin
        // [GIVEN] A pageextension using movefirst() in the layout area
        // [WHEN]  Business logic in the same compilation unit is called
        // [THEN]  It executes — movefirst() declaration does not block compilation
        Assert.AreEqual('movefirst ok', Helper.GetLabel(), 'Helper must return expected label');
    end;

    [Test]
    procedure Movefirst_WrongValue_Fails()
    begin
        // Negative: asserterror proves the assertion fires for wrong values
        asserterror Assert.AreEqual('Wrong', Helper.GetLabel(), '');
        Assert.ExpectedError('Wrong');
    end;

    [Test]
    procedure Movefirst_CalcTotal_Positive()
    begin
        // Positive: price * qty computes correctly
        Assert.AreEqual(250, Helper.CalcTotal(25, 10), 'CalcTotal(25,10) must be 250');
    end;

    [Test]
    procedure Movefirst_CalcTotal_Zero()
    begin
        // Edge case: zero quantity gives zero total
        Assert.AreEqual(0, Helper.CalcTotal(25, 0), 'CalcTotal with zero qty must be 0');
    end;

    [Test]
    procedure Movefirst_FormatCode_NonEmpty()
    begin
        // Positive: code is wrapped in brackets
        Assert.AreEqual('[ITM001]', Helper.FormatCode('ITM001'), 'FormatCode must wrap in brackets');
    end;

    [Test]
    procedure Movefirst_FormatCode_Empty()
    begin
        // Edge case: empty code still formats with brackets
        Assert.AreEqual('[]', Helper.FormatCode(''), 'FormatCode with empty code must produce []');
    end;
}
