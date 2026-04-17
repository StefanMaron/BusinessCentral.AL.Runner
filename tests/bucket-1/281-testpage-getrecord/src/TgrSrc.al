/// Source objects for TestPage.GetRecord coverage (issue #119).
/// GetRecord(var Rec) must return the record the page is currently positioned on.
table 131000 "TGR Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

page 131001 "TGR Page"
{
    PageType = List;
    SourceTable = "TGR Table";
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
