/// <summary>
/// The report-execution-entry-point coverage this suite originally proved (SaveAs(Xml)
/// actually running a report's triggers/body, Report.Run(Report::X) vs instance .Run() vs
/// .SaveAs) is real BC semantics and migrated upstream to the al-language corpus
/// (tests/al-language, handlers/TestReportRunExecution.al). Only this one test stays: it
/// asserts a runner-specific OutOfScope classification (the runner has no service tier to
/// render a non-ProcessingOnly report with, so it must fail LOUDLY naming the surface
/// rather than silently no-op), not real BC behavior.
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
}
