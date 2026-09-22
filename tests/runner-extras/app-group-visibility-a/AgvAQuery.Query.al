// #4447: a query in this app group, so Query Metadata (2000000142) has a subject in every
// group. Without one, every "group X does not see group Y's query" assertion is vacuous --
// the failure mode this issue was filed around.
query 62605 "AGV A Query"
{
    Caption = 'AGV A Query Caption';
    QueryType = Normal;

    elements
    {
        dataitem(AgvAQueryRows; "AGV A Table")
        {
            column(CodeCol; "Code") { }
        }
    }
}
