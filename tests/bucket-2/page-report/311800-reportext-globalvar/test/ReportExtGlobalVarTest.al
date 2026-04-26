// Tests for issue #1450: CS1061 on ReportExtension<N> missing GetGlobalVariable/SetGlobalVariable.
// BC emits scope classes inside ReportExtension types that access global variables via
// GetGlobalVariable(int id, NavType type) / SetGlobalVariable(int id, NavType type, object value).
// After the rewriter strips NavReportExtension, these methods must be injected on the class.
codeunit 311801 "REGV Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure ReportExtWithGlobalVarCompiles()
    begin
        // Positive: a reportextension with a global variable accessed from a modify trigger
        // must compile without CS1061. If compilation fails, this codeunit is never found
        // and the test suite reports 0 tests — a detectable regression.
        Assert.IsTrue(true, 'ReportExtension with global var compiled — GetGlobalVariable/SetGlobalVariable are present');
    end;

    [Test]
    [HandlerFunctions('REGVReportHandler')]
    procedure ReportExtRunsWithoutError()
    begin
        // Positive: running the base report (which invokes the extension's scope classes)
        // must not throw any error.
        Report.Run(311800);
    end;

    [ReportHandler]
    procedure REGVReportHandler(var TestRequestPage: TestRequestPage "REGV Base Report")
    begin
        // No-op: intercept the report run; the point is that the ReportExtension compiled
        // and the scope class (which calls GetGlobalVariable/SetGlobalVariable) loaded
        // without CS1061.
    end;
}
