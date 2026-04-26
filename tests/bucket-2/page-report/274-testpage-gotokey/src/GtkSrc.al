/// Source objects for TestPage.GoToKey / GoToRecord tests (issue #868).
table 118000 "GTK Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[50]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

page 118001 "GTK List"
{
    PageType = List;
    SourceTable = "GTK Table";
    layout
    {
        area(Content)
        {
            repeater(R)
            {
                field("No."; Rec."No.") { }
                field(Name; Rec.Name) { }
            }
        }
    }
}
