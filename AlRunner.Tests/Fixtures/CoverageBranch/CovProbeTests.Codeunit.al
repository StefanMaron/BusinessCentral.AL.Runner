codeunit 50922 "Cov Probe Tests RXT"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Cov Assert RXT";

    [Test]
    procedure Run_FlagTrue_ReturnsTwo()
    var
        Probe: Codeunit "Cov Probe RXT";
    begin
        Assert.AreEqual(2, Probe.Run(true), 'expected 2');
    end;

    // Deliberately fails: AlRunner.Tests/CoverageTests.cs pins this Error call to a
    // specific AL source line and asserts the "line L" AL Runner prints in the stack
    // trace is identical whether or not --coverage (and therefore the StmtHit/CStmtHit
    // Cecil rewrite) is active — the regression this issue calls out as most likely to
    // break and least likely to be noticed.
    [Test]
    procedure DeliberateFailure_ForStackTraceLineRegression()
    begin
        Error('deliberate failure for stack-trace-line regression check');
    end;
}
