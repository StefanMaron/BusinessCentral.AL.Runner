table 89000 "XI Item"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

xmlport 89000 "XI Item Port"
{
    Direction = Both;
    Format = VariableText;

    schema
    {
        textelement(Root)
        {
            tableelement(ItemRec; "XI Item")
            {
                fieldelement(Id; ItemRec.Id) { }
                fieldelement(Name; ItemRec.Name) { }
            }
        }
    }
}

codeunit 89000 "XI Src"
{
    procedure CallExport(): Boolean
    var
        XP: XmlPort "XI Item Port";
        OutStr: OutStream;
    begin
        XP.Export();
        exit(true);
    end;

    procedure CallImport(): Boolean
    var
        XP: XmlPort "XI Item Port";
        InStr: InStream;
    begin
        XP.Import();
        exit(true);
    end;

    procedure CallRun(): Boolean
    var
        XP: XmlPort "XI Item Port";
    begin
        XP.Run();
        exit(true);
    end;

    procedure CallSetDestination(): Boolean
    var
        XP: XmlPort "XI Item Port";
        OutStr: OutStream;
    begin
        XP.SetDestination(OutStr);
        exit(true);
    end;

    procedure CallSetSource(): Boolean
    var
        XP: XmlPort "XI Item Port";
        InStr: InStream;
    begin
        XP.SetSource(InStr);
        exit(true);
    end;

    procedure CallSetTableView(): Boolean
    var
        XP: XmlPort "XI Item Port";
        Rec: Record "XI Item";
    begin
        XP.SetTableView(Rec);
        exit(true);
    end;

    procedure CallCurrentPath(): Text
    var
        XP: XmlPort "XI Item Port";
    begin
        exit(XP.CurrentPath());
    end;

    procedure CallFieldDelimiter(): Boolean
    var
        XP: XmlPort "XI Item Port";
    begin
        XP.FieldDelimiter('"');
        exit(true);
    end;

    procedure GetFieldDelimiter(): Text[10]
    var
        XP: XmlPort "XI Item Port";
    begin
        exit(XP.FieldDelimiter());
    end;

    procedure CallFieldSeparator(): Boolean
    var
        XP: XmlPort "XI Item Port";
    begin
        XP.FieldSeparator(',');
        exit(true);
    end;

    procedure GetFieldSeparator(): Text[10]
    var
        XP: XmlPort "XI Item Port";
    begin
        exit(XP.FieldSeparator());
    end;

    procedure CallFilename(): Boolean
    var
        XP: XmlPort "XI Item Port";
    begin
        XP.Filename('test.xml');
        exit(true);
    end;

    procedure GetFilename(): Text
    var
        XP: XmlPort "XI Item Port";
    begin
        exit(XP.Filename());
    end;

    procedure CallRecordSeparator(): Boolean
    var
        XP: XmlPort "XI Item Port";
    begin
        XP.RecordSeparator(';');
        exit(true);
    end;

    procedure GetRecordSeparator(): Text[10]
    var
        XP: XmlPort "XI Item Port";
    begin
        exit(XP.RecordSeparator());
    end;

    procedure CallTableSeparator(): Boolean
    var
        XP: XmlPort "XI Item Port";
    begin
        XP.TableSeparator(';');
        exit(true);
    end;

    procedure GetTableSeparator(): Text[10]
    var
        XP: XmlPort "XI Item Port";
    begin
        exit(XP.TableSeparator());
    end;

    procedure CallTextEncoding(): Boolean
    var
        XP: XmlPort "XI Item Port";
    begin
        XP.TextEncoding(TextEncoding::UTF8);
        exit(true);
    end;
}
