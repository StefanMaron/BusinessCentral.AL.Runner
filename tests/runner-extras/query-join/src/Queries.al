// InnerJoin and LeftOuterJoin coverage migrated upstream to the al-language corpus
// (tests/al-language, query/). Only the RightOuterJoin case stays here: it asserts a
// runner-specific OutOfScope classification (loud-failures rule), not real BC semantics.

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
