/// <summary>
/// Binds and compiles cleanly, yet NEVER RUNS: its sibling object in the same module is
/// EMIT-EXCLUDED, and a module missing objects must not run at all, so Program.cs empties
/// the module's sources. That is why the loss is bigger than the excluded-object count —
/// the excluded set names 1 object, and 2 [Test] procedures go missing.
/// </summary>
codeunit 61310 "Emit Excl Part Healthy"
{
    Subtype = Test;

    var
        Assert: Codeunit "Emit Excl Part Assert";

    [Test]
    procedure ExcludedSuiteHealthy_NeverRuns()
    begin
        Assert.AreEqual(3, 1 + 2, 'this test is dropped with its module, not executed');
    end;
}
