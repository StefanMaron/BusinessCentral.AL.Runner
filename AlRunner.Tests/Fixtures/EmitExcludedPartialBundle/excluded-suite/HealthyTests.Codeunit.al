/// <summary>
/// Binds and compiles cleanly, and RUNS. Its sibling object in the same module is
/// EMIT-EXCLUDED, which used to empty the module's sources and take this test down with it —
/// so the loss was bigger than the excluded-object count: 1 object named, 2 [Test] procedures
/// gone. Since #3476 the sibling is dropped on its own (a test codeunit nothing else in the
/// module names), this test executes, and only the sibling's own test is reported SKIPPED.
/// The suite error and exit code 3 are unchanged: the run still covers less than it found.
/// </summary>
codeunit 61310 "Emit Excl Part Healthy"
{
    Subtype = Test;

    var
        Assert: Codeunit "Emit Excl Part Assert";

    [Test]
    procedure ExcludedSuiteHealthy_StillRuns()
    begin
        Assert.AreEqual(3, 1 + 2, 'the surviving object of a partially-excluded module must run');
    end;
}
