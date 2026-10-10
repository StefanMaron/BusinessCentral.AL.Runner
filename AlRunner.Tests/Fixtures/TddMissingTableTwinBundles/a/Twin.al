// A codeunit and a table that share a name, in one file of the bundle the tests depend on. The tests assign a field the
// table does not declare: --tdd must add it to the TABLE of bundle "a" (found by name AND type), not to the codeunit.
codeunit 65200 "Twin"
{
    procedure Existing(): Integer
    begin
        exit(1);
    end;
}

table 65200 "Twin"
{
    fields
    {
        field(1; "Code"; Integer) { }
    }
    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
