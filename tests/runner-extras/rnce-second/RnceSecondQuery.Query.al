query 66225 "Rnce Second Query"
{
    QueryType = Normal;
    Caption = 'Rnce Second Query Caption';

    elements
    {
        dataitem(Rows; "Rnce Second Row")
        {
            column(No; "No.") { }
            column(Descr; Description) { }
        }
    }
}
