codeunit 72860 "TPI B Target"
{
    procedure Placeholder()
    begin
    end;
}

table 72861 "TPI Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }
}

page 72862 "TPI Card"
{
    PageType = Card;
    SourceTable = "TPI Rec";
    layout { area(Content) { field(PK; Rec.PK) { } } }
    trigger OnOpenPage() var T: Codeunit "TPI B Target"; R: Integer; begin R := T.MIdOpen(1); end;
}
