// Host table for the served-lookup bundle. The three fields are a matched set differing in one
// property each, so a test that passes for a reason other than the one it names fails on a
// sibling:
//
//   "Served"     -- TableRelation to a table that HAS a LookupPageId. Served.
//   "No Page"    -- TableRelation to a table that has NONE. Refused, naming that cause.
//   "No Relation"-- no relation at all. Refused, naming THAT cause.
//
// None of the three declares an OnLookup trigger, and none of their controls on "Tls Card"
// does either, so the TableRelation is the only thing that can distinguish them.
table 65793 "Tls Host"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { DataClassification = CustomerContent; }
        // The subject: a relation the runner can follow all the way to a page.
        field(2; "Served"; Code[20])
        {
            DataClassification = CustomerContent;
            TableRelation = "Tls Related"."Code";
        }
        // Green control for the first refusal: a relation that resolves to a table with no
        // lookup page. There is genuinely nothing to open.
        field(3; "No Page"; Code[20])
        {
            DataClassification = CustomerContent;
            TableRelation = "Tls Orphan"."Code";
        }
        // Green control for the second refusal: nothing to resolve at all.
        field(4; "No Relation"; Code[20]) { DataClassification = CustomerContent; }
        // The FILTERED relation. Same target as "Served", plus a where() arm — so the lookup
        // page must show only the unblocked rows. A runner that opened the page without
        // applying the relation's filters would offer REL-BLOCKED, which real BC never does,
        // and the test would then be able to select a value Validate rejects.
        field(5; "Filtered Code"; Code[20])
        {
            DataClassification = CustomerContent;
            TableRelation = "Tls Related"."Code" where(Blocked = const(false));
        }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
