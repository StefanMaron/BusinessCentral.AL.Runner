xmlport 58400 "XmlPort Items"
{
    Direction = Import;
    Format = Xml;

    schema
    {
        textelement(Root)
        {
            tableelement(ItemRec; "XmlPort Item")
            {
                fieldelement(Id; ItemRec.Id) { }
                fieldelement(Name; ItemRec.Name) { }
            }
        }
    }
}
