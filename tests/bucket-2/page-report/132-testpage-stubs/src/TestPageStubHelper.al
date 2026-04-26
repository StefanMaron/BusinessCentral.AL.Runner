table 56310 "TestPage Stub Item"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

page 56310 "TestPage Stub Card"
{
    PageType = Card;
    SourceTable = "TestPage Stub Item";

    layout
    {
        area(Content)
        {
            field(NameField; Rec.Name) { }
        }
    }
}
