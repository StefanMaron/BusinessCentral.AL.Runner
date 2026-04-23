query 229001 "Simple Item Query"
{
    QueryType = Normal;
    elements
    {
        dataitem(Item; "Query Item")
        {
            column(ItemNo; "No.") { }
            column(Description; Description) { }
            column(UnitPrice; "Unit Price") { }
            column(Qty; Quantity) { }
        }
    }
}
