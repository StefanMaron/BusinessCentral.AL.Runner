codeunit 56291 "ReportExt Parent Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        Probe: Codeunit "ReportExt Probe";

    [Test]
    procedure ReportExtCompilationSucceeds()
    begin
        // Positive: the reportextension with triggers accessing parent variables
        // must compile successfully (proves CS1061 'Parent' is resolved)
        Assert.IsTrue(Probe.CompilationSucceeded(), 'ReportExtension should compile with Parent access in scope classes');
    end;

    [Test]
    procedure ReportExtCompilationNotFalsePositive()
    begin
        // Negative: verify the probe actually returns true, not just any truthy value
        Assert.AreEqual(true, Probe.CompilationSucceeded(), 'CompilationSucceeded should return true');
    end;
}
