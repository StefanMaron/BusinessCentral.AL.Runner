/// The page `TriggerlessAction` / `TriggerlessRef` open through `RunObject`, and the only
/// observable in this bundle that says the RunObject was actually PERFORMED.
///
/// It exists because of #2975. Those two arms used to assert that the invoke raised BC's
/// "Unhandled UI" error, on the reasoning that a page opened with no `[PageHandler]` bound is
/// refused. That is true of `Page.Run`, and it is NOT true of a RunObject action: eight real
/// service tiers (corpus codeunit 60285 "TPARONH Tests", 27.0 / 27.3 / 27.5 / 28.0-28.4) say
/// the target opens unattended and AL is never told. The old arms therefore had a falsified
/// BC-behaviour claim written into a runner-local test, and with nothing raised any more,
/// "the invoke completed" alone would pass against a runner that performed nothing at all.
///
/// So the RunObject target is a page of its own that records its own opening. `TriggerlessAction`
/// used to name the HOST page, which cannot carry this trigger: every arm in the suite opens the
/// host with `OpenEdit()`, so an OnOpenPage there would fire for all of them and the tag would
/// prove nothing about the action.
page 64547 "Par RunObject Target"
{
    PageType = Card;
    SourceTable = "Par Row";
    ApplicationArea = All;

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
        }
    }

    trigger OnOpenPage()
    var
        OpenLog: Record "Par Open Log";
    begin
        OpenLog.Log('RUNOBJ-OPENED');

        // The second observable, for the LinkedPageAction arm (#2942 x #2975). An action's
        // RunPageLink is applied as ordinary filters on the TARGET's cursor, so a link that was
        // applied is visible here as a filter and a link that was never applied is not. Without
        // it the linked arm and the unlinked one would assert exactly the same thing, because
        // neither raises anything any more. What the link SELECTS stays upstream.
        if Rec.GetFilter("No.") <> '' then
            OpenLog.Log('RUNOBJ-LINKED');
    end;
}
