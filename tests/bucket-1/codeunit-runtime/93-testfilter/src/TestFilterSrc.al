/// Source objects for TestFilter method coverage.
/// Tests: Ascending (get/set), CurrentKey (get), SetCurrentKey (set),
/// SetFilter (store), GetFilter (retrieve).

table 96000 "TPF Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
        key(ByName; Name) { }
    }
}

page 96000 "TPF Test Page"
{
    PageType = List;
    SourceTable = "TPF Test Record";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(IdField; Rec.Id) { ApplicationArea = All; }
                field(NameField; Rec.Name) { ApplicationArea = All; }
                field(AmountField; Rec.Amount) { ApplicationArea = All; }
            }
        }
    }
}
