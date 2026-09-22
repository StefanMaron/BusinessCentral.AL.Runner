// #4447: a query in this app group, so Query Metadata (2000000142) has a subject in every
// group. Without one, every "group X does not see group Y's query" assertion is vacuous --
// the failure mode this issue was filed around.
query 62615 "AGV B Query"
{
    Caption = 'AGV B Query Caption';
    QueryType = Normal;

    elements
    {
        dataitem(AgvBQueryRows; "AGV B Table")
        {
            column(CodeCol; "Code") { }
        }
    }
}
