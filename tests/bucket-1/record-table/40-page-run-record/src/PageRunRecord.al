// Renumbered from 56400 to avoid collision in new bucket layout (#1385).
table 1056400 "PRR Item"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

page 56400 "PRR Item List"
{
    PageType = List;
    SourceTable = "PRR Item";
}

codeunit 56400 "PRR Caller"
{
    procedure ShowItem(var Item: Record "PRR Item"): Integer
    begin
        Page.Run(Page::"PRR Item List", Item);
        exit(42);
    end;

    procedure ShowItemCurrRec(var Item: Record "PRR Item"): Integer
    begin
        // The issue mentions rule/action code in ShowRelatedInformation
        // calling Page.Run(..., Rec). Variants:
        Page.RunModal(Page::"PRR Item List", Item);
        exit(43);
    end;
}
