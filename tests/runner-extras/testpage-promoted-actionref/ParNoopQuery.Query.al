/// A query that exists solely to be the TARGET of an action's `RunObject = query ...`.
/// Sibling of "Par Noop Report", "Par Noop Runner" and "Par Noop XmlPort".
///
/// A query has no trigger of any kind, so unlike the other three this fixture cannot record
/// its own execution — which is why the arm that invokes it asserts only on the refusal. That
/// is a real limit of the object kind, not a weaker test: there is nothing in a query for a
/// regression to run.
query 64551 "Par Noop Query"
{
    QueryType = Normal;

    elements
    {
        dataitem(Row; "Par Row")
        {
            column(No; "No.") { }
        }
    }
}
