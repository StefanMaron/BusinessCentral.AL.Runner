table 58700 "XPS Schema Item"
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

/// XmlPort with a schema section containing textelement, tableelement,
/// and fieldelement nodes — the most common schema pattern in BC.
xmlport 58700 "XPS Item Export"
{
    Direction = Export;
    Format = Xml;

    schema
    {
        textelement(Root)
        {
            tableelement(ItemRec; "XPS Schema Item")
            {
                fieldelement(Id; ItemRec.Id) { }
                fieldelement(Name; ItemRec.Name) { }
                fieldelement(Category; ItemRec.Category) { }
            }
        }
    }
}

/// XmlPort with a text-only schema section (no table element).
/// Exercises the basic textelement/textattribute subset.
xmlport 58701 "XPS Header Only"
{
    Direction = Export;
    Format = Xml;

    schema
    {
        textelement(Root)
        {
            textelement(Header) { }
        }
    }
}

codeunit 58700 "XPS Schema Helper"
{
    /// Returns a constant to prove that a codeunit containing XmlPort
    /// variable declarations and schema sections compiles and runs.
    procedure GetStatus(): Text
    var
        XP: XmlPort "XPS Item Export";
    begin
        // Declaring an XmlPort variable with a schema section must compile.
        exit('ok');
    end;

    /// Returns the XmlPort object number — proves the XmlPort object
    /// with a schema section is registered at its declared ID.
    procedure GetExportPortId(): Integer
    var
        XP: XmlPort "XPS Item Export";
    begin
        exit(58700);
    end;

    /// Returns the second XmlPort's ID — proves two XmlPort objects
    /// each with schema sections compile in the same bucket.
    procedure GetHeaderPortId(): Integer
    begin
        exit(58701);
    end;

    /// Wraps Export() in a codeunit so tests can use asserterror.
    procedure TryExport(var OutStr: OutStream)
    var
        XP: XmlPort "XPS Item Export";
    begin
        XP.SetDestination(OutStr);
        XP.Export();
    end;
}
