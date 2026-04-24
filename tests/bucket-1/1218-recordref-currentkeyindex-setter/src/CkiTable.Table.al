table 1218000 "CKI Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
        field(3; SortVal; Integer) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
        key(BySortVal; SortVal) { }
    }
}
