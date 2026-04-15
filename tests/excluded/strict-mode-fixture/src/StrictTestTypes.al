table 59950 "Strict Test Item"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

xmlport 59950 "Strict Test XmlPort"
{
    Direction = Import;
    Format = Xml;

    schema
    {
        textelement(Root)
        {
            tableelement(Item; "Strict Test Item")
            {
                fieldelement(Id; Item.Id) { }
                fieldelement(Name; Item.Name) { }
            }
        }
    }
}
