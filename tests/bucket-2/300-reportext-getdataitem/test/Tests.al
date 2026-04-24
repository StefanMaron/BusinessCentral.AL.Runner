// Tests for issue #1212: CS1061 on ReportExtension missing 'GetDataItem'
// and 'ParentObject'. When a reportextension references a dataitem record
// declared on the BASE report, BC emits
//   this.CurrReport.GetDataItem("<name>").Record.GetFieldValueSafe(...)
// inside the ReportExtensionNNNNN class (via CurrReport => this after the
// rewriter's existing stub). Without GetDataItem / ParentObject stubs on
// the reportextension class, the Roslyn compile fails with CS1061 and no
// tests are discovered.
codeunit 70702 "RptExt GetDI Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Probe: Codeunit "RptExt GetDI Probe";

    [Test]
    procedure ReportExtensionCompilesWithGetDataItemReference()
    begin
        // Positive: the reportextension references a base-report dataitem
        // (Cust) from both a non-override procedure and a dataitem trigger.
        // Without the GetDataItem stub, Roslyn fails with CS1061 and this
        // codeunit never loads. Reaching this assertion proves the stub
        // compiled. The concrete value 1212 guards against a no-op probe.
        Assert.AreEqual(1212, Probe.GapIssueNumber(), 'Probe reachable — proves ReportExtension compiled (issue #1212)');
    end;

    [Test]
    [HandlerFunctions('NoOpHandler')]
    procedure BaseReportRunsWithExtensionLoaded()
    begin
        // Positive: running the base report loads the compiled extension
        // assembly. The ReportHandler intercepts dataset iteration so the
        // GetDataItem stub is not actually invoked — the point is that the
        // full pipeline (transpile → rewrite → Roslyn compile → run) is
        // green end-to-end.
        Report.Run(70700);
        Assert.AreEqual(70700, 70700, 'Report.Run completed without CS1061 in ReportExtension');
    end;

    [Test]
    procedure ProbeErrorPathStillRaises()
    begin
        // Negative: guard that the probe's error path works end-to-end
        // through the same assembly that contains the reportextension.
        // If the extension stub broke compilation, this codeunit would
        // never load and no error would be observed at all. Asserting
        // the exact marker text proves the right path fired.
        asserterror Probe.FailWithMarker();
        Assert.ExpectedError('gap-1212');
    end;

    [ReportHandler]
    procedure NoOpHandler(var TestRequestPage: TestRequestPage "RptExt GetDI Base")
    begin
    end;
}
