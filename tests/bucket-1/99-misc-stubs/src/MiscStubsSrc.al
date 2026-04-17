table 111000 "Misc Stubs Table"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

codeunit 111001 MiscStubsSrc
{
    procedure DoXmlNodeIsDocumentType(Node: XmlNode): Boolean
    begin
        exit(Node.IsXmlDocumentType());
    end;

    procedure DoXmlNodeAsDocumentType(Node: XmlNode): XmlDocumentType
    begin
        exit(Node.AsXmlDocumentType());
    end;

    procedure DoNavAppGetArchiveRecordRef(TableId: Integer; var RecRef: RecordRef): Boolean
    begin
        NavApp.GetArchiveRecordRef(TableId, RecRef);
        exit(not RecRef.IsEmpty());
    end;

    procedure DoNavAppGetResource(ResName: Text; var IStream: InStream): Boolean
    begin
        NavApp.GetResource(ResName, IStream);
        exit(true);
    end;

    procedure DoRecordIdGetRecord(RecId: RecordId): RecordRef
    begin
        exit(RecId.GetRecord());
    end;
}
