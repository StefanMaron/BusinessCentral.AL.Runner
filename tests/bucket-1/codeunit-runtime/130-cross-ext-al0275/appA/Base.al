// Renumbered from 56300 to avoid collision in new bucket layout (#1385).
table 1056300 "Test Item 130"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[100]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

page 56300 "Test Item Card 130"
{
    SourceTable = "Test Item 130";
    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { }
            field(Description; Rec.Description) { }
        }
    }
}
