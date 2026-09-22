xmlport 66226 "Rnce Second XmlPort"
{
    Caption = 'Rnce Second XmlPort Caption';
    Format = Xml;
    Direction = Export;

    schema
    {
        textelement(Root)
        {
            tableelement(Rows; "Rnce Second Row")
            {
                fieldelement(No; Rows."No.") { }
            }
        }
    }
}
