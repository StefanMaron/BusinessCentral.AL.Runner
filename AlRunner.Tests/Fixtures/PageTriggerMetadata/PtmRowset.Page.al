// The two rowset triggers, which RunnerPageInstance.DeclaresRowsetTrigger now reads off these
// same flags (#3439, #3447).
page 70664 "PTM Rowset"
{
    PageType = List;
    SourceTable = "PTM Row";
    layout { area(Content) { repeater(Rows) { field("No."; Rec."No.") { ApplicationArea = All; } } } }

    trigger OnFindRecord(Which: Text): Boolean
    begin
        exit(Rec.Find(Which));
    end;

    trigger OnNextRecord(Steps: Integer): Integer
    begin
        exit(Rec.Next(Steps));
    end;
}
