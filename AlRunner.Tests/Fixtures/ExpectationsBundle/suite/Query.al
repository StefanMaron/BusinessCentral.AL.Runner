// RightOuterJoin: the runner's in-memory nested-loop join executor cannot
// faithfully reproduce it, so opening this query throws a typed
// RunnerOutOfScopeException (see AlRunner.QueryJoin/JoinExecutor.cs). The
// fixture tests below open it WITHOUT asserterror so the throw reaches the
// test executor and can be classified against the manifest.
query 60802 "Expct Right Join"
{
    QueryType = Normal;

    elements
    {
        dataitem(Customer; "Expct Customer")
        {
            column(CustNo; "No.") { }

            dataitem(Ord; "Expct Order")
            {
                DataItemLink = "Customer No." = Customer."No.";
                SqlJoinType = RightOuterJoin;

                column(EntryNo; "Entry No.") { }
            }
        }
    }
}
