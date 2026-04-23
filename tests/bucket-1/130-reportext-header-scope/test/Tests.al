// Tests for issue #1013: CS1061 on ReportExtension scope classes missing 'Parent'.
// The BC compiler generates scope classes (e.g. Header_a45_OnAfterAfterGetRecord_Scope)
// inside ReportExtension types that inherit NavTriggerMethodScope<ReportExtensionNNNNN>.
// After the rewriter strips NavReportExtension and replaces the base with AlScope,
// the Parent property must be present and _parent must be assigned in the constructor.
//
// A compilation failure (CS1061 on 'Parent') would cause 0 tests to run, which
// is itself a detectable regression — any CS1061 means this codeunit is never found.
codeunit 57101 "ReportExt Header Scope Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure HeaderTriggerScopeCompiles()
    begin
        // Positive: the ReportExtension with modify(Header) { trigger OnAfterAfterGetRecord() }
        // must compile without CS1061 on the generated Header_a45_OnAfterAfterGetRecord_Scope class.
        // The scope class inherits NavTriggerMethodScope<ReportExtension57100> and references .Parent.
        // If compilation fails, this codeunit is never loaded and 0 tests run.
        //
        // This is the EXACT pattern from the telemetry:
        //   CS1061 on 'ReportExtension50500.Header_a45_OnAfterAfterGetRecord_Scope':
        //   does not contain a definition for 'Parent'
        Assert.IsTrue(true, 'Scope class compiled successfully — Parent is defined');
    end;

    [Test]
    [HandlerFunctions('NoOpReportHandler')]
    procedure HeaderTriggerReportRunsWithoutError()
    begin
        // Positive: running the base report (which causes the ReportExtension scope classes
        // to be compiled and loaded) must not throw any error.
        // The ReportHandler intercepts the run so no actual report output is produced.
        Report.Run(57100);
    end;

    [ReportHandler]
    procedure NoOpReportHandler(var TestRequestPage: TestRequestPage "TestBaseReportForExt")
    begin
        // No-op: intercept the report run and do nothing.
        // The point is that the ReportExtension compiled and the handler fires without error.
    end;
}
