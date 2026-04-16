/// Helper codeunit that exercises InStream.EOS() detection.
codeunit 84000 "InStream EOS Src"
{
    procedure EmptyStreamIsEOS(): Boolean
    var
        Rec: Record "IEos Data";
        InStr: InStream;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateInStream(InStr);
        exit(InStr.EOS());
    end;

    procedure NonEmptyStreamIsNotEOSAtStart(): Boolean
    var
        Rec: Record "IEos Data";
        InStr: InStream;
        OutStr: OutStream;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateOutStream(OutStr);
        OutStr.WriteText('Hello');
        Rec.Content.CreateInStream(InStr);
        exit(not InStr.EOS());
    end;

    procedure StreamIsEOSAfterReadingAll(): Boolean
    var
        Rec: Record "IEos Data";
        InStr: InStream;
        OutStr: OutStream;
        Line: Text;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateOutStream(OutStr);
        OutStr.WriteText('Hello');
        Rec.Content.CreateInStream(InStr);
        InStr.ReadText(Line);
        exit(InStr.EOS());
    end;

    /// Read a 4-byte stream in 2-byte chunks — loop must iterate exactly twice.
    procedure CountChunksUsingEOS(): Integer
    var
        Rec: Record "IEos Data";
        InStr: InStream;
        OutStr: OutStream;
        Chunk: Text;
        Count: Integer;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateOutStream(OutStr);
        OutStr.WriteText('AABB');   // 4 bytes
        Rec.Content.CreateInStream(InStr);
        Count := 0;
        while not InStr.EOS() do begin
            InStr.ReadText(Chunk, 2);   // read 2 bytes at a time
            Count += 1;
        end;
        exit(Count);  // expects 2: 'AA' then 'BB'
    end;
}

table 84000 "IEos Data"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Content; Blob) { }
    }
    keys
    {
        key(PK; "Entry No.") { }
    }
}
