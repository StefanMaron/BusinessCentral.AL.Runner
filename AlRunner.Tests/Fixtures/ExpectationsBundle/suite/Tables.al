// Parent + child tables backing the RightOuterJoin query. The query is the
// fixture's deterministic typed-OOS surface: the in-memory join executor throws
// RunnerOutOfScopeException with reason "query-join-rightouterjoin-not-implemented"
// (permanently out of scope, so this fixture cannot rot into a passing test).

table 60800 "Expct Customer"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Name"; Text[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

table 60801 "Expct Order"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Customer No."; Code[20]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
