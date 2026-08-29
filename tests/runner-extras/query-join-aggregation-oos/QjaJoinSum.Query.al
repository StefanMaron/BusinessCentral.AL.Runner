query 64532 "Qja Join Sum"
{
    QueryType = Normal;

    elements
    {
        dataitem(Order; "Qja Order")
        {
            column(CustNo; "Cust No.") { }
            column(TotalAmount; Amount)
            {
                Method = Sum;
            }
            dataitem(Customer; "Qja Customer")
            {
                DataItemLink = "No." = Order."Cust No.";
                column(CustName; Name) { }
            }
        }
    }
}
