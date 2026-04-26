/// Source objects for TestPart method coverage tests.
table 97300 "TPT Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// ListPart page used as the subpage part on the card.
page 97300 "TPT Lines"
{
    PageType = ListPart;
    SourceTable = "TPT Record";
    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(IdField; Rec.Id) { ApplicationArea = All; }
                field(NameField; Rec.Name) { ApplicationArea = All; }
            }
        }
    }
}

/// Card page containing a part — gives tests a TestPart to work with.
page 97301 "TPT Card"
{
    PageType = Card;
    layout
    {
        area(Content)
        {
            part(Lines; "TPT Lines") { ApplicationArea = All; }
        }
    }
}
