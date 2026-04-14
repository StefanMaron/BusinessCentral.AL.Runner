table 56901 BlobTable
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Data; Blob) { }
    }
}

codeunit 56902 BlobHelper
{
    procedure WriteText(var Rec: Record BlobTable; InputText: Text)
    var
        OStr: OutStream;
    begin
        Rec.Data.CreateOutStream(OStr);
        OStr.WriteText(InputText);
    end;

    procedure ReadText(var Rec: Record BlobTable): Text
    var
        IStr: InStream;
        Result: Text;
    begin
        Rec.Data.CreateInStream(IStr);
        IStr.ReadText(Result);
        exit(Result);
    end;

    procedure HasValue(var Rec: Record BlobTable): Boolean
    begin
        exit(Rec.Data.HasValue());
    end;
}
