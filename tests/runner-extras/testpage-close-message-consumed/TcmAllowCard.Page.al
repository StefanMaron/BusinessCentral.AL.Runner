/// <summary>
/// The counterpart to "Tcm Error Card": an OnQueryClosePage that ALLOWS the close, and counts
/// how many times it was raised. The card above refuses every close, so it can only ever
/// measure the refused path; this one measures the allowed one, which is where the constraint
/// on #3593 lives.
///
/// Why the count has to be observable at all. #3593 adds a close attempt to the RunModal round
/// trip so a refused close is delivered twice, matching BC. Corpus codeunit 60276 "MQC Tests"
/// measured on a real service tier that the same OK().Invoke() shape raises OnQueryClosePage
/// exactly ONCE when the trigger allows the close -- so the extra attempt must be reachable only
/// after a refusal. A runner that simply attempted the close twice would satisfy the refused-path
/// arm and break this one, which is the point of having both.
///
/// The count is written to a table rather than held in a page global because the page instance
/// the round trip builds is not one the test holds afterwards.
/// </summary>
page 65864 "Tcm Allow Card"
{
    PageType = Card;
    SourceTable = "Tcm Row";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
            field("Set ID"; Rec."Set ID") { ApplicationArea = All; }
        }
    }

    trigger OnQueryClosePage(CloseAction: Action): Boolean
    var
        Row: Record "Tcm Row";
    begin
        if not Row.Get('QCP') then begin
            Row.Init();
            Row."No." := 'QCP';
            Row."Set ID" := 0;
            Row.Insert();
        end;
        Row."Set ID" := Row."Set ID" + 1;
        Row.Modify();
        exit(true);
    end;
}
