/// Temporary-record helper with a Blob field, exercised by the blob stream tests.
table 59580 "BST TempBlob"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Blob; Blob) { }
    }
    keys { key(PK; PK) { } }
}

/// Helper codeunit exercising Blob.CreateOutStream + OutStream.WriteText for
/// writing and Blob.CreateInStream + InStream.ReadText for reading — the
/// blob-via-stream surface issue #472 names.
codeunit 59580 "BST Src"
{
    procedure WriteAndRead(InputText: Text): Text
    var
        TempBlob: Record "BST TempBlob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Text;
    begin
        TempBlob.Blob.CreateOutStream(OutStr);
        OutStr.WriteText(InputText);
        TempBlob.Blob.CreateInStream(InStr);
        InStr.ReadText(Result);
        exit(Result);
    end;

    procedure WrittenBlobHasValue(InputText: Text): Boolean
    var
        TempBlob: Record "BST TempBlob" temporary;
        OutStr: OutStream;
    begin
        TempBlob.Blob.CreateOutStream(OutStr);
        OutStr.WriteText(InputText);
        exit(TempBlob.Blob.HasValue);
    end;

    procedure FreshBlobHasNoValue(): Boolean
    var
        TempBlob: Record "BST TempBlob" temporary;
    begin
        exit(TempBlob.Blob.HasValue);
    end;

}
