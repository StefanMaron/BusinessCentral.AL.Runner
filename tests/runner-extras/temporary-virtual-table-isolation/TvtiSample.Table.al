// A table local to this bundle, so the non-temporary Field-table control below asserts
// against field metadata this suite owns end to end -- no Base Application floor, and the
// exact same TableNo the temporary half uses.
table 64581 "TVTI Sample"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; Amount; Decimal) { }
        field(3; Description; Text[50]) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}

