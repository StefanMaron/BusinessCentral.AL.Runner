// Two queries over the same two tables. They differ only in SqlJoinType so the
// test can compare InnerJoin vs LeftOuterJoin semantics over identical data.

// InnerJoin: a customer with no order row is dropped from the result.
query 60310 "QJ Cust Orders Inner"
{
    QueryType = Normal;
    OrderBy = ascending(EntryNo);

    elements
    {
        dataitem(Customer; "QJ Customer")
        {
            column(CustNo; "No.") { }
            column(CustName; "Name") { }

            dataitem(Ord; "QJ Order")
            {
                DataItemLink = "Customer No." = Customer."No.";
                SqlJoinType = InnerJoin;

                column(EntryNo; "Entry No.") { }
                column(Amount; "Amount") { }
            }
        }
    }
}

// LeftOuterJoin: a customer with no order row is KEPT, with null/default child columns.
query 60311 "QJ Cust Orders Left"
{
    QueryType = Normal;
    OrderBy = ascending(CustNo);

    elements
    {
        dataitem(Customer; "QJ Customer")
        {
            column(CustNo; "No.") { }
            column(CustName; "Name") { }

            dataitem(Ord; "QJ Order")
            {
                DataItemLink = "Customer No." = Customer."No.";
                SqlJoinType = LeftOuterJoin;

                column(EntryNo; "Entry No.") { }
                column(Amount; "Amount") { }
            }
        }
    }
}

// RightOuterJoin: an in-memory join sub-case the runner cannot faithfully reproduce.
// Opening this query must throw RunnerOutOfScopeException with a SPECIFIC named reason
// rather than returning wrong/partial rows (loud-failures rule).
query 60312 "QJ Cust Orders Right"
{
    QueryType = Normal;

    elements
    {
        dataitem(Customer; "QJ Customer")
        {
            column(CustNo; "No.") { }

            dataitem(Ord; "QJ Order")
            {
                DataItemLink = "Customer No." = Customer."No.";
                SqlJoinType = RightOuterJoin;

                column(EntryNo; "Entry No.") { }
            }
        }
    }
}
