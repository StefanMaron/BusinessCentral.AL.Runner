/// Source objects for tooltip_control_property test suite.
/// Verifies that Tooltip on page controls compiles without error.
table 80950 "TTP Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
        field(3; Amount; Decimal) { }
        field(4; Active; Boolean) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 80950 "TTP Test Page"
{
    PageType = Card;
    SourceTable = "TTP Test Record";

    layout
    {
        area(Content)
        {
            group(General)
            {
                field(IdField; Rec.Id)
                {
                    ApplicationArea = All;
                    Tooltip = 'Specifies the unique identifier for the record';
                }
                field(NameField; Rec.Name)
                {
                    ApplicationArea = All;
                    Tooltip = 'Specifies the name of the record';
                }
                field(AmountField; Rec.Amount)
                {
                    ApplicationArea = All;
                    Tooltip = 'Specifies the monetary amount associated with this record';
                }
                field(ActiveField; Rec.Active)
                {
                    ApplicationArea = All;
                    Tooltip = 'Indicates whether this record is currently active';
                }
            }
        }
    }
}
