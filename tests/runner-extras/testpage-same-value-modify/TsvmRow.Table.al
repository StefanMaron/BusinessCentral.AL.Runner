/// Row under test. Its OnModify bumps a counter that lives in ANOTHER table (Tsvm Trace),
/// deliberately: a counter field on this row would be written by OnModify itself, making it a
/// genuinely changed field and destroying the very condition -- "no field value on this row
/// differs from the one it was loaded with" -- that the suite exists to measure.
table 65761 "Tsvm Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Note; Text[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }

    trigger OnModify()
    var
        Trace: Record "Tsvm Trace";
    begin
        Trace.Bump();
    end;
}
