// Renumbered from 95000 to avoid collision in new bucket layout (#1385).
table 1095000 "RS Test Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
