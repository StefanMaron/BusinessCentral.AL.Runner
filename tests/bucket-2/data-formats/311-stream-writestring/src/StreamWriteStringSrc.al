/// Helper codeunit exercising OutStream.Write(Text) which transpiles to
/// MockStream.ALWriteString, and InStream.Read(Text) which reads it back.
codeunit 59610 "SWST Src"
{
    /// Write a Text value via OutStream.Write (generates ALWriteString),
    /// then read it back via InStream.Read — round-trip.
    procedure WriteAndRead(InputText: Text): Text
    var
        TempBlob: Record "SWST TempBlob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Text;
    begin
        TempBlob.Data.CreateOutStream(OutStr);
        OutStr.Write(InputText);
        TempBlob.Data.CreateInStream(InStr);
        InStr.Read(Result);
        exit(Result);
    end;

    /// Write a fixed-length Text[50] value via OutStream.Write.
    procedure WriteAndReadFixed(InputText: Text[50]): Text[50]
    var
        TempBlob: Record "SWST TempBlob" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Result: Text[50];
    begin
        TempBlob.Data.CreateOutStream(OutStr);
        OutStr.Write(InputText);
        TempBlob.Data.CreateInStream(InStr);
        InStr.Read(Result);
        exit(Result);
    end;
}

table 59610 "SWST TempBlob"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Data; Blob) { }
    }
    keys { key(PK; PK) { } }
}
