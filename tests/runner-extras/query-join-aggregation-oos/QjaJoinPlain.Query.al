query 64536 "Qja Join Plain"
{
    // Sibling of "Qja Join Sum" with NO aggregated column — the join+aggregation guard in
    // RecordPatches.QueryProjection.cs must not trip on an ordinary join query.
    QueryType = Normal;

    elements
    {
        dataitem(Order; "Qja Order")
        {
            column(CustNo; "Cust No.") { }
            dataitem(Customer; "Qja Customer")
            {
                DataItemLink = "No." = Order."Cust No.";
                column(CustName; Name) { }
            }
        }
    }
}
