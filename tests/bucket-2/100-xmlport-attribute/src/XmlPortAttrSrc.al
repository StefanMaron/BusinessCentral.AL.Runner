table 64000 "XPA Item"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Category; Code[20]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// XmlPort that uses textattribute and fieldattribute schema nodes.
/// These are XML attributes (name="value") rather than XML elements (<tag>value</tag>).
/// The runner must compile this without errors — the schema is declarative;
/// no actual I/O occurs in these tests.
xmlport 64000 "XPA Items"
{
    Direction = Export;
    Format = Xml;

    schema
    {
        textelement(Root)
        {
            /// textattribute — maps a text variable to an XML attribute on the parent element.
            textattribute(Version)
            {
                XmlName = 'version';
                trigger OnBeforePassVariable()
                begin
                    Version := '1.0';
                end;
            }
            tableelement(ItemRec; "XPA Item")
            {
                XmlName = 'Item';
                /// fieldattribute — maps a record field to an XML attribute.
                fieldattribute(ItemId; ItemRec.Id)
                {
                    XmlName = 'id';
                }
                fieldelement(Name; ItemRec.Name)
                {
                    XmlName = 'name';
                }
            }
        }
    }
}

/// Helper codeunit — in the same compilation unit as the xmlport_attribute XmlPort.
/// Proves that a codeunit that declares an XmlPort variable (with attribute schema)
/// compiles and runs correctly without invoking I/O.
codeunit 64000 "XPA Helper"
{
    procedure GetPortId(): Integer
    var
        XP: XmlPort "XPA Items";
    begin
        // Declaring XP compiles; return a constant to prove execution reached here.
        exit(64000);
    end;

    procedure GetStatus(): Text
    var
        XP: XmlPort "XPA Items";
    begin
        exit('ok');
    end;
}
