/// <summary>
/// #1771 — the runner-specific edge left behind once static Report.Run/RunModal(id, ...)
/// execute for real: an id the runner's metadata cache can never learn about must still fail
/// LOUDLY (RunnerOutOfScopeException, out-of-scope message), never silently return as if the
/// report had run. Whether a RESOLVABLE report id executes end-to-end (dataset iteration,
/// OnPostReport, etc.) is plain BC behaviour and belongs in the al-language corpus, not here.
/// </summary>
codeunit 60955 "SRO Tests"
{
    Subtype = Test;

    // No AL object in this bundle declares this id, so the runner's NCLMetadata cache never
    // learns it — the one construction path SyncStaticRun cannot satisfy.
    var
        UnresolvableReportId: Integer;

    trigger OnRun()
    begin
    end;

    local procedure Init()
    begin
        UnresolvableReportId := 999999999;
    end;

    [Test]
    procedure StaticRunModal_UnresolvableReportId_ThrowsOutOfScope_NotSilentNoOp()
    begin
        Init();

        asserterror Report.RunModal(UnresolvableReportId, false, false);

        if StrPos(GetLastErrorText(), 'out-of-scope: NavReport.Run/RunModal') = 0 then
            Error('Expected an out-of-scope error naming NavReport.Run/RunModal, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'not-yet-implemented') = 0 then
            Error('Expected the not-yet-implemented reason, got: %1', GetLastErrorText());
    end;

    [Test]
    procedure StaticRun_UnresolvableReportId_ThrowsOutOfScope_NotSilentNoOp()
    begin
        Init();

        asserterror Report.Run(UnresolvableReportId, false, false);

        if StrPos(GetLastErrorText(), 'out-of-scope: NavReport.Run/RunModal') = 0 then
            Error('Expected an out-of-scope error naming NavReport.Run/RunModal, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'not-yet-implemented') = 0 then
            Error('Expected the not-yet-implemented reason, got: %1', GetLastErrorText());
    end;
}
