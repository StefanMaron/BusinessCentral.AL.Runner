/// <summary>
/// The DELETED page — WatchPageMetadataReloadDeleteTests removes this file between cycle 1
/// and cycle 2. Nothing else in this fixture references "RPR Gone" (deliberately: it only
/// needs to exist long enough to register once, so deleting it never breaks a compile
/// reference elsewhere). Its cycle-2 AL_RUNNER_TRACE_PAGE_METADATA output must be silent for
/// this page id — proving BcRuntime.ResetForNewBundleReload()'s AlPageMetadataRegistry.Clear()
/// actually took effect and nothing resurrects a page the bundle no longer declares.
/// </summary>
page 70202 "RPR Gone"
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
