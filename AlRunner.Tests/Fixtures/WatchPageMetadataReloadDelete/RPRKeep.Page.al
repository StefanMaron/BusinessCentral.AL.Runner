/// <summary>
/// The SURVIVING page — untouched by the edit WatchPageMetadataReloadDeleteTests makes
/// between cycles. Its cycle-2 AL_RUNNER_TRACE_PAGE_METADATA re-registration line proves
/// the shadow-snapshot replay in BcCompiler.Incremental.cs (see _radPageMetadataByModule):
/// this app group's own files are otherwise unchanged this cycle (RPR Gone.Page.al was
/// deleted, which IS a change to the bundle, but not to THIS file), so nothing but that
/// replay re-registers this page's metadata after BcRuntime.ResetForNewBundleReload()
/// cleared it at the top of the cycle.
/// </summary>
page 70201 "RPR Keep"
{
    PageType = List;
    SourceTable = "RPR Row";
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            repeater(Entries)
            {
                field("Code"; Rec."Code") { ApplicationArea = All; }
            }
        }
    }
}
