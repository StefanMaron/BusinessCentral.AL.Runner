table 56250 "Diag Test Item"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Item No."; Code[20]) { }
        field(3; Quantity; Decimal) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

xmlport 56251 "Diag XmlPort"
{
    Direction = Import;
    Format = Xml;

    schema
    {
        textelement(Root)
        {
            tableelement(ItemRec; "Diag Test Item")
            {
                fieldelement(EntryNo; ItemRec."Entry No.") { }
                fieldelement(ItemNo; ItemRec."Item No.") { }
            }
        }
    }
}

query 56252 "Diag Item Query"
{
    QueryType = Normal;

    elements
    {
        dataitem(Item; "Diag Test Item")
        {
            column(EntryNo; "Entry No.") { }
            column(ItemNo; "Item No.") { }
            column(Qty; Quantity) { Method = Sum; }
        }
    }
}

/// Logic codeunit that wraps XmlPort and Query calls for test access.
codeunit 56253 "Diag Logic"
{
    // ---- XmlPort lifecycle (should NOT throw) ----

    procedure XmlPortDeclareAndReturn(): Text
    var
        XP: XmlPort "Diag XmlPort";
    begin
        // Declaring an XmlPort variable and running other logic must work.
        exit('xmlport-ok');
    end;

    procedure XmlPortInvoke()
    var
        XP: XmlPort "Diag XmlPort";
    begin
        // Invoke() returns null — no crash expected.
    end;

    // ---- XmlPort operations that MUST throw ----

    procedure TryXmlPortImport(var InStr: InStream)
    var
        XP: XmlPort "Diag XmlPort";
    begin
        XP.SetSource(InStr);
        XP.Import();
    end;

    procedure TryXmlPortExport(var OutStr: OutStream)
    var
        XP: XmlPort "Diag XmlPort";
    begin
        XP.SetDestination(OutStr);
        XP.Export();
    end;

    procedure TryStaticXmlPortImport(var InStr: InStream)
    begin
        XmlPort.Import(XmlPort::"Diag XmlPort", InStr);
    end;

    procedure TryStaticXmlPortExport(var OutStr: OutStream)
    begin
        XmlPort.Export(XmlPort::"Diag XmlPort", OutStr);
    end;

    // ---- Query lifecycle (should NOT throw) ----

    procedure QueryDeclareAndReturn(): Text
    var
        Q: Query "Diag Item Query";
    begin
        exit('query-ok');
    end;

    procedure QuerySetRangeAndClose()
    var
        Q: Query "Diag Item Query";
    begin
        Q.SetRange(EntryNo, 1, 100);
        Q.Close();
    end;

    procedure QuerySetFilterAndClose()
    var
        Q: Query "Diag Item Query";
    begin
        Q.SetFilter(ItemNo, 'ITEM*');
        Q.TopNumberOfRows(5);
        Q.Close();
    end;

    // ---- Query operations that MUST throw ----

    procedure TryQueryOpen()
    var
        Q: Query "Diag Item Query";
    begin
        Q.Open();
    end;

    procedure TryQueryRead()
    var
        Q: Query "Diag Item Query";
    begin
        Q.Read();
    end;
}
