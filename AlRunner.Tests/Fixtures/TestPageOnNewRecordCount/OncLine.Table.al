// "Header No." is the FIRST field of the primary key — the shape in which a page's single-valued
// filter is copied onto a row the page starts, and the shape of every Base Application document
// line.
//
// Descr deliberately has NO OnValidate: the question here is a count, and a validate that can
// raise would turn an over-firing count into an error message instead of a number.
table 70643 "ONC Line"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Header No."; Code[20]) { }
        field(2; "Line No."; Integer) { }
        field(3; Descr; Text[50]) { }
    }

    keys
    {
        key(PK; "Header No.", "Line No.") { Clustered = true; }
    }
}
