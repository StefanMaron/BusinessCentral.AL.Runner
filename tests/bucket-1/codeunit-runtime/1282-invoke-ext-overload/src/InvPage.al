page 1282001 "Inv Ext Pg"
{
    PageType = Card;
    SourceTable = "Inv Ext Tbl";

    layout
    {
        area(Content)
        {
            field(EntryNo; Rec."Entry No.") { }
        }
    }

    procedure GetBaseNumber(): Integer
    begin
        exit(100);
    end;
}
