// Parent + child tables backing the RightOuterJoin query. The query is the
// fixture's deterministic typed-OOS surface: the in-memory join executor throws
// RunnerOutOfScopeException with reason
// "not-yet-implemented — query-join-rightouterjoin-link-type: …".
//
// That reason used to read "query-join-rightouterjoin-not-implemented", and this
// comment used to call the surface "permanently out of scope, so this fixture
// cannot rot into a passing test". It is not permanent: RightOuterJoin is valid
// AL that real BC executes, so it is a gap the runner means to close, and the old
// anchor let an AL [TryFunction] absorb it (#2966). What keeps the fixture from
// rotting is that the executor still refuses the shape at all — which
// tests/runner-extras/standalone-suites/query-join pins from the AL side.

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
