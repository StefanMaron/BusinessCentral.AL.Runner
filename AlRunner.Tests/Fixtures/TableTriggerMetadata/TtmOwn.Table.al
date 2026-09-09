// A table declaring its own OnRename and nothing else, with no tableextension over it: the
// base-table arm of the computation on its own.
table 70741 "TTM Own"
{
    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys { key(PK; "No.") { Clustered = true; } }

    trigger OnRename()
    begin
    end;
}
