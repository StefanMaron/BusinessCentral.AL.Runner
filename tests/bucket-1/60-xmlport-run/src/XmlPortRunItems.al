xmlport 60200 "XmlPort Run Items"
{
    Direction = Import;
    Format = Xml;

    schema
    {
        textelement(Root)
        {
            tableelement(ItemRec; "XmlPort Run Item")
            {
                fieldelement(Id; ItemRec.Id) { }
                fieldelement(Name; ItemRec.Name) { }
            }
        }
    }
}
