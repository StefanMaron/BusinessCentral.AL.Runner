/// <summary>
/// A PASSING run that drives the runner's FlowField recursion guard. The AL here asserts
/// only what the al-language corpus already asserts against a real service tier
/// (TestCalcFormulaFlowFieldValueTests.Record_CalcFields_SelfReferencingFormula_RaisesTheRecursionError);
/// it is duplicated as a runner fixture purely because FlowFieldDiagnosticNoiseTests needs
/// a small, base-app-free bundle it can spawn and read stderr from.
/// </summary>
codeunit 60843 "FFR Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "FFR Assert";

    local procedure Initialize()
    var
        Header: Record "FFR Header";
        Line: Record "FFR Line";
    begin
        Header.DeleteAll();
        Line.DeleteAll();

        Header.Init();
        Header."No." := 'D1';
        Header.Insert();

        Line.Init();
        Line."Entry No." := 1;
        Line."Doc No." := 'D1';
        Line.Amount := 100;
        Line."Ref Amount" := 100;
        Line.Insert();
    end;

    [Test]
    procedure CalcFields_SelfReferencingFormula_RaisesTheRecursionError()
    var
        Header: Record "FFR Header";
    begin
        // [GIVEN] a seeded document
        Initialize();
        Header.Get('D1');

        // [WHEN] [THEN] the self-referencing formula is refused, not recursed
        asserterror Header.CalcFields("Self Ref Amount");
        Assert.ExpectedError('This can be caused by recursive function calls', GetLastErrorText());

        // [THEN] the refusal is specific to that one formula
        Initialize();
        Clear(Header);
        Header.Get('D1');
        Header.CalcFields("Total Amount");
        Assert.AreEqual('100', Format(Header."Total Amount"),
            'a refused self-referencing formula must not disturb the other FlowFields');
    end;

    [Test]
    procedure CalcFields_MutuallyReferencingFormulas_RaiseTheRecursionError()
    var
        Header: Record "FFR Header";
    begin
        // [GIVEN] "Cycle A" reads "Cycle B" and "Cycle B" reads "Cycle A"
        Initialize();
        Header.Get('D1');

        // [WHEN] [THEN] the cycle is bounded and reported, not run until the stack dies.
        // This is the deep path: the guard only bites 50 levels down, so the runner's
        // internal diagnostic for it is a 300-frame stack trace.
        asserterror Header.CalcFields("Cycle A");
        Assert.ExpectedError('This can be caused by recursive function calls', GetLastErrorText());

        Initialize();
        Clear(Header);
        Header.Get('D1');
        Header.CalcFields("Total Amount");
        Assert.AreEqual('100', Format(Header."Total Amount"),
            'a refused cyclic formula must not disturb the other FlowFields');
    end;
}
