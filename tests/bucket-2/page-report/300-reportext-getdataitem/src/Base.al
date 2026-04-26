// Base table and report for the reportextension GetDataItem test.
table 70700 "RptExt GetDI Cust"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

table 70701 "RptExt GetDI Item"
{
    fields
    {
        field(1; "No."; Code[20]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

report 70700 "RptExt GetDI Base"
{
    dataset
    {
        dataitem(Cust; "RptExt GetDI Cust")
        {
            column(Name; Name) { }
        }
    }
}
