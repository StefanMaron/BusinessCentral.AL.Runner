// The query whose COLUMN IDS are what is at stake. They are assigned by the BC compiler and
// reach the runtime only through the SymbolReference.json this bundle's Emit writes and
// registers (BcCompiler.EmitAndRegisterBundleQuerySymbols) — see the driving test's header.
query 70621 "WQS Sum"
{
    QueryType = Normal;

    elements
    {
        dataitem(Row; "WQS Row")
        {
            column(CustNo; "Cust No.") { }
            column(TotalAmount; Amount)
            {
                Method = Sum;
            }
        }
    }
}
