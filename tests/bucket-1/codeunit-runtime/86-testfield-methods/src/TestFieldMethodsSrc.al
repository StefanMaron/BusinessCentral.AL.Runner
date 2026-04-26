/// Table and page used by the TestField-methods test suite.
/// Tests AsBoolean, AsInteger, AsDate, AsTime, AssertEquals, ValidationErrorCount, etc.
table 86100 "TFM Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Qty; Integer) { }
        field(4; Flag; Boolean) { }
        field(5; Amt; Decimal) { }
        field(6; PostDate; Date) { }
        field(7; PostTime; Time) { }
        field(8; PostDateTime; DateTime) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 86100 "TFM Card"
{
    PageType = Card;
    SourceTable = "TFM Record";

    layout
    {
        area(Content)
        {
            field(NameField; Rec.Name) { }
            field(QtyField; Rec.Qty) { }
            field(FlagField; Rec.Flag) { }
            field(AmtField; Rec.Amt) { }
            field(DateField; Rec.PostDate) { }
            field(TimeField; Rec.PostTime) { }
            field(DateTimeField; Rec.PostDateTime) { }
        }
    }
}
