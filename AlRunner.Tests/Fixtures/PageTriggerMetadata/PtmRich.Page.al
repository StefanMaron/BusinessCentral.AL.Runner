// Declares the ten triggers a page can carry besides OnFindRecord/OnNextRecord, so the audit
// line for this page names all ten and names neither of the two it does not declare.
page 70661 "PTM Rich"
{
    PageType = List;
    SourceTable = "PTM Row";
    layout { area(Content) { repeater(Rows) { field("No."; Rec."No.") { ApplicationArea = All; } } } }

    trigger OnInit() begin end;

    trigger OnOpenPage() begin end;

    trigger OnClosePage() begin end;

    trigger OnAfterGetRecord() begin end;

    trigger OnNewRecord(BelowxRec: Boolean) begin end;

    trigger OnInsertRecord(BelowxRec: Boolean): Boolean begin exit(true); end;

    trigger OnModifyRecord(): Boolean begin exit(true); end;

    trigger OnDeleteRecord(): Boolean begin exit(true); end;

    trigger OnQueryClosePage(CloseAction: Action): Boolean begin exit(true); end;

    trigger OnAfterGetCurrRecord() begin end;
}
