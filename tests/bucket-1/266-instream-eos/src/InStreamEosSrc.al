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

    procedure CountLinesUsingEOS(): Integer
    var
        Rec: Record "IEos Data";
        InStr: InStream;
        OutStr: OutStream;
        Line: Text;
        Count: Integer;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateOutStream(OutStr);
        OutStr.WriteText('Line1');
        OutStr.WriteText('Line2');
        Rec.Content.CreateInStream(InStr);
        Count := 0;
        while not InStr.EOS() do begin
            InStr.ReadText(Line);
            Count += 1;
        end;
        exit(Count);
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
