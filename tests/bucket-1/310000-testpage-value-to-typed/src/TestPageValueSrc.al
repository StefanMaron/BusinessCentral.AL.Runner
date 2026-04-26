table 70140700 "TPV Read Record"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[100]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

page 70140700 "TPV Read Card"
{
    PageType = Card;
    SourceTable = "TPV Read Record";

    layout
    {
        area(Content)
        {
            field(NoField; Rec."No.") { }
            field(DescriptionField; Rec.Description) { }
        }
    }
}
