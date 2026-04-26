// Renumbered from 58400 to avoid collision in new bucket layout (#1385).
table 1058400 "XmlPort Item"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}
