// The RELATED table a "Tls Host" field's TableRelation points at. It declares LookupPageId,
// which is the property that gives a triggerless lookup a page to open -- the whole difference
// between this bundle and testpage-lookup-tablerelation-oos, whose related table declares none
// and therefore still refuses.
table 65790 "Tls Related"
{
    DataClassification = CustomerContent;
    LookupPageId = "Tls Related List";

    fields
    {
        field(1; "Code"; Code[20]) { DataClassification = CustomerContent; }
        field(2; Descr; Text[50]) { DataClassification = CustomerContent; }
        // The column a "Tls Host"."Filtered Code" where() arm narrows on, so the suite can
        // tell a lookup that applied the relation's filters from one that opened the page
        // unfiltered. Without a filterable column the filter call is unobservable.
        field(3; Blocked; Boolean) { DataClassification = CustomerContent; }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
