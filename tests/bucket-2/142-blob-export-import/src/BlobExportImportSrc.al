/// Table holding a blob field — used by all helpers in this suite.
table 50142 "BLEI Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; PK; Integer) { }
        field(2; Data; Blob) { }
    }
    keys
    {
        key(PK; PK) { Clustered = true; }
    }
}

/// Helper codeunit that exercises Blob stream round-trip and Export/Import stubs.
codeunit 50142 "BLEI Helper"
{
    /// Write text into the blob field and return it via InStream for read-back.
    procedure WriteAndRead(InputText: Text): Text
    var
        Rec: Record "BLEI Item" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Text;
    begin
        Rec.PK := 1;
        Rec.Insert(false);
        Rec.Data.CreateOutStream(OutStr);
        OutStr.WriteText(InputText);
        Rec.Modify(false);
        Rec.Data.CreateInStream(InStr);
        InStr.ReadText(Result);
        exit(Result);
    end;

    /// Return whether the blob has a value after writing text to it.
    procedure HasValueAfterWrite(): Boolean
    var
        Rec: Record "BLEI Item" temporary;
        OutStr: OutStream;
    begin
        Rec.PK := 2;
        Rec.Insert(false);
        Rec.Data.CreateOutStream(OutStr);
        OutStr.WriteText('some data');
        Rec.Modify(false);
        exit(Rec.Data.HasValue());
    end;

    /// Return the blob byte length after writing a known string.
    procedure LengthAfterWrite(InputText: Text): Integer
    var
        Rec: Record "BLEI Item" temporary;
        OutStr: OutStream;
    begin
        Rec.PK := 3;
        Rec.Insert(false);
        Rec.Data.CreateOutStream(OutStr);
        OutStr.WriteText(InputText);
        Rec.Modify(false);
        exit(Rec.Data.Length());
    end;

    /// Call Export on the blob — runner stubs this as a no-op returning false
    /// (file system access is not available in the runner).
    procedure TryExport(): Boolean
    var
        Rec: Record "BLEI Item" temporary;
        OutStr: OutStream;
    begin
        Rec.PK := 4;
        Rec.Insert(false);
        Rec.Data.CreateOutStream(OutStr);
        OutStr.WriteText('exportdata');
        Rec.Modify(false);
        exit(Rec.Data.Export('/tmp/al-runner-blob-test.bin'));
    end;

    /// Call Import on the blob — runner stubs this as a no-op returning false
    /// (file system access is not available in the runner).
    procedure TryImport(): Boolean
    var
        Rec: Record "BLEI Item" temporary;
    begin
        Rec.PK := 5;
        Rec.Insert(false);
        exit(Rec.Data.Import('/tmp/al-runner-blob-test.bin'));
    end;
}
