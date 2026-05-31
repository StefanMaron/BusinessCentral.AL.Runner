// Parent table with a FlowField Count of child rows, plus a child table.
// The FlowField "Child Count" exercises the temp-table filter-visitor path when
// used in SetRange (the exact pattern Purch.-Post uses with Purchase Line
// "Matched Order Lines").

table 60500 "FF Visitor Parent"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Integer) { }
        field(2; "Description"; Text[50]) { }
        field(10; "Child Count"; Integer)
        {
            FieldClass = FlowField;
            CalcFormula = count("FF Visitor Child" where("Parent No." = field("No.")));
            Editable = false;
        }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

table 60501 "FF Visitor Child"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Parent No."; Integer) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
        key(ParentKey; "Parent No.") { }
    }
}
