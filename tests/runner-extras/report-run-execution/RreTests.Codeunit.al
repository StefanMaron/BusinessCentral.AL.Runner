/// <summary>
/// Control experiment for report EXECUTION entry points.
///
/// Observed while fixing the Integer virtual table: a probe report did not execute
/// at all through Report.Run() — OnPreReport never fired, nothing was raised and
/// nothing was written. That is a silent no-op, the same class as an empty virtual
/// table or emit-only state lost on a cache hit, and it matters because a report
/// that never runs produces no PDF and raises no error — precisely what the
/// Pageworks pdf-render (64 tests) and asserterror-not-thrown (16 tests) clusters
/// report.
///
/// That probe was ProcessingOnly with no rendering layout, so the no-op might have
/// been specific to that shape rather than general. These tests separate the
/// possibilities over a REAL table with REAL stored rows, so no virtual-table
/// provider is involved:
///
///   shape A (ProcessingOnly, no layout)  vs  shape B (layout, columns)
///   Report.Run(Report::X)  vs  instance .Run()  vs  instance .SaveAs(Xml)
///
/// Each assertion names the entry point, so a fix cannot repair one path while
/// silently leaving another a no-op.
/// </summary>
codeunit 61892 "RRE Tests"
{
    Subtype = Test;

    local procedure SeedRows()
    var
        Row: Record "RRE Row";
    begin
        Row.DeleteAll();
        Row.Init();
        Row."Entry No." := 1;
        Row.Name := 'first';
        Row.Insert();
        Row.Init();
        Row."Entry No." := 2;
        Row.Name := 'second';
        Row.Insert();
        Row.Init();
        Row."Entry No." := 3;
        Row.Name := 'third';
        Row.Insert();
    end;

    [Test]
    procedure InstanceRun_NonProcessingOnly_ThrowsOutOfScopeForRendering()
    var
        Probe: Report "RRE Layout Report";
    begin
        // DESIGNED behaviour, pinned so it cannot silently become a no-op: a report
        // that is not ProcessingOnly must attempt to render after its lifecycle
        // triggers, and the runner has no service tier to render with — so it must
        // fail LOUDLY naming the surface, never return quietly.
        SeedRows();
        Clear(Probe);
        Probe.UseRequestPage(false);
        asserterror Probe.Run();
        if StrPos(GetLastErrorText(), 'out-of-scope') = 0 then
            Error('Expected an out-of-scope error naming report rendering, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'Layout') = 0 then
            Error('Expected the error to name the layout surface, got: %1', GetLastErrorText());
    end;

    [Test]
    procedure InstanceSaveAsXml_ExecutesTriggersAndBody()
    var
        Probe: Report "RRE Layout Report";
        TempBlob: Codeunit "Temp Blob";
        ResultOutStream: OutStream;
    begin
        // The dataset (Xml) path is in scope for the runner and is what Pageworks drives.
        SeedRows();
        Clear(Probe);
        TempBlob.CreateOutStream(ResultOutStream);
        Probe.SaveAs('', ReportFormat::Xml, ResultOutStream);

        if not Probe.DidPreReportRun() then
            Error('instance SaveAs(Xml): OnPreReport never fired — the report did not execute at all.');
        if Probe.RowsProcessed() <> 3 then
            Error('instance SaveAs(Xml): expected 3 body executions, got %1', Probe.RowsProcessed());
    end;

    [Test]
    procedure StaticSaveAsXml_ProducesADatasetNamingTheRows()
    var
        TempBlob: Codeunit "Temp Blob";
        ResultOutStream: OutStream;
        ResultInStream: InStream;
        Dataset: Text;
        Line: Text;
    begin
        // The static form cannot report through report globals, so assert on the OUTPUT:
        // a dataset that actually names a seeded row proves the body ran.
        SeedRows();
        TempBlob.CreateOutStream(ResultOutStream);
        Report.SaveAs(Report::"RRE Layout Report", '', ReportFormat::Xml, ResultOutStream);

        TempBlob.CreateInStream(ResultInStream);
        while not ResultInStream.EOS() do begin
            ResultInStream.ReadText(Line);
            Dataset += Line;
        end;

        if Dataset = '' then
            Error('Report.SaveAs(Xml) wrote an EMPTY stream — the report produced no dataset at all.');
        if StrPos(Dataset, 'second') = 0 then
            Error('Report.SaveAs(Xml) dataset does not contain the seeded row "second" — the data item body did not run. Dataset was: %1', CopyStr(Dataset, 1, 300));
    end;
}
