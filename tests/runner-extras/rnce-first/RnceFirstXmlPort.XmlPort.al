xmlport 66206 "Rnce First XmlPort"
{
    Caption = 'Rnce First XmlPort Caption';
    Format = Xml;
    Direction = Export;

    schema
    {
        textelement(Root)
        {
            tableelement(Rows; "Rnce First Row")
            {
                fieldelement(No; Rows."No.") { }
            }
        }
    }
}
