// The card-with-lines shape the New() path needs. Two link entries deliberately:
//   "No." = field("No.")  — read from the parent row
//   Kind  = const('K1')   — a constant, the arm the corpus test does not exercise
// Both are part of "TNV Line"'s primary key, so BC's InitRecordFromFilters copies both onto a
// New() row, and the runner must therefore validate both.
page 70403 "TNV Card"
{
    PageType = Card;
    SourceTable = "TNV Header";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
            part(Lines; "TNV Lines")
            {
                ApplicationArea = All;
                SubPageLink = "No." = field("No."), Kind = const('K1');
            }
        }
    }
}
