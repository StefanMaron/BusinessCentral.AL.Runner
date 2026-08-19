/// <summary>
/// #1957 fixture: OnOpenPage marks the row named 'MARK' as touched, in an
/// independently-varying record it does not open — a page-object trigger effect,
/// not a Rec-cursor side effect. Under the bug, RecordPatches.ResetForReload()
/// left the page's "already (successfully|un-)loaded" bookkeeping stale across a
/// --watch reload, so the second cycle onward silently built a control-less
/// skeleton NCLMetaForm instead of running LoadMetadata() again — TestPage then
/// caught the resulting NRE and fell back to record-only access, and this trigger
/// never ran. WatchPageMetadataReloadTests proves it runs on every cycle by
/// checking `Touched` afterwards, not by checking that OpenView() didn't throw.
/// </summary>
page 70021 "WPMR List"
{
    PageType = List;
    SourceTable = "WPMR Row";
    Editable = false;
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            repeater(Entries)
            {
                field("Code"; Rec."Code") { ApplicationArea = All; }
                field(Touched; Rec.Touched) { ApplicationArea = All; }
            }
        }
    }

    trigger OnOpenPage()
    var
        Row: Record "WPMR Row";
    begin
        if Row.Get('MARK') then begin
            Row.Touched := true;
            Row.Modify(true);
        end;
    end;
}
