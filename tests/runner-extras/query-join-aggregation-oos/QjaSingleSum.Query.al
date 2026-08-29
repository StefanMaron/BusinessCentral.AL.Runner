query 64533 "Qja Single Sum"
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
        }
    }
}
