/// Control arm: an action declared DIRECTLY on the page. Must already dispatch — proves the
/// suite's own Invoke()/handler plumbing works before the pageextension arms are trusted.
page 64521 "Pad Host Page"
{
    PageType = List;
    SourceTable = "Pad Row";
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action(DirectAction)
            {
                ApplicationArea = All;
                Caption = 'Direct Action';

                trigger OnAction()
                var
                    Row: Record "Pad Row";
                begin
                    Row.Log('DIRECT');
                end;
            }
        }
    }
}
