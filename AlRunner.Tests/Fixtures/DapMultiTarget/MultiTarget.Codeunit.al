/// <summary>
/// #3820: ONE source line carrying more than one executable statement.
///
/// Line 29 holds two assignments, and BOTH run. A DAP breakpoint on that line used to bind
/// one of them — DapBreakpointResolver.Resolve stopped at the first exact line match — so it
/// fired once instead of at each target. Which one it bound was arbitrary: the instrumented
/// statement indexes come out of a HashSet.
///
/// Line 28 is the single-statement control, so a fact about line 29 cannot pass by an
/// implementation that stops twice on every line.
///
/// The line numbers below are asserted against exactly by DapMultiTargetLineTests.
/// Keep them in sync.
/// </summary>
codeunit 60311 "Dap Multi Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Dap Multi Assert";

    [Test]
    procedure TwoStatementsOnOneLine()
    var
        First: Integer;
        Second: Integer;
    begin
        First := 1;
        Second := 2; First := First + Second;
        Assert.AreEqual(3, First, 'both statements on the shared line ran');
        Assert.AreEqual(2, Second, 'the first of the two ran');
    end;
}
