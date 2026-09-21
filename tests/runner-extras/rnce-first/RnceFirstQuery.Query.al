query 66205 "Rnce First Query"
{
    QueryType = Normal;
    Caption = 'Rnce First Query Caption';

    elements
    {
        dataitem(Rows; "Rnce First Row")
        {
            column(No; "No.") { }
            column(Descr; Description) { }
        }
    }
}
