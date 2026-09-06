// The key field list uses ';' where AL requires ','. BC's parser reports this as
// AL0104 "Syntax error, ')' expected" at line 14, column 20 — the diagnostic the
// runner must surface. See issue #2949.
table 60940 "Malformed Key Row"
{
    DataClassification = SystemMetadata;
    fields
    {
        field(1; "A"; Code[20]) { }
        field(2; "B"; Code[20]) { }
    }
    keys
    {
        key(PK; "A"; "B") { Clustered = true; }
    }
}
