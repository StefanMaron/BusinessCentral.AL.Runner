/// Table with a Media field used by the media-stream tests.
table 303010 "MST Media Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Content; Media) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Helper blob table used to create real InStream / OutStream values.
table 303011 "MST Blob Table"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Blob; Blob) { }
    }
    keys { key(PK; PK) { } }
}

/// Helper codeunit that exercises Media.ImportStream and Media.ExportStream.
codeunit 303012 "MST Helper"
{
    procedure ImportFromStream(var Rec: Record "MST Media Table"; var Source: InStream): Boolean
    begin
        Rec.Content.ImportStream(Source, '');
        exit(Rec.Content.HasValue());
    end;

    procedure ExportToStream(var Rec: Record "MST Media Table"; var Target: OutStream): Boolean
    begin
        Rec.Content.ExportStream(Target);
        exit(true);
    end;

    procedure HasValue(var Rec: Record "MST Media Table"): Boolean
    begin
        exit(Rec.Content.HasValue());
    end;

    procedure MakeBlobInStream(var BlobRec: Record "MST Blob Table" temporary; var InStr: InStream)
    var
        OutStr: OutStream;
    begin
        BlobRec.Blob.CreateOutStream(OutStr);
        OutStr.WriteText('test-content');
        BlobRec.Blob.CreateInStream(InStr);
    end;

    procedure MakeBlobOutStream(var BlobRec: Record "MST Blob Table" temporary; var OutStr: OutStream)
    begin
        BlobRec.Blob.CreateOutStream(OutStr);
    end;
}
