table 1320423 "IS Position Data"
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

codeunit 1320418 "InStream Position Src"
{
    procedure ReadFromPosition(): Text
    var
        Rec: Record "IS Position Data";
        InStr: InStream;
        OutStr: OutStream;
        Result: Text;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateOutStream(OutStr);
        OutStr.WriteText('Hello World');
        Rec.Content.CreateInStream(InStr);
        InStr.Position := 6;
        InStr.ReadText(Result);
        exit(Result);
    end;

    procedure SetPositionOutOfRange(): Text
    var
        Rec: Record "IS Position Data";
        InStr: InStream;
        OutStr: OutStream;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateOutStream(OutStr);
        OutStr.WriteText('Hi');
        Rec.Content.CreateInStream(InStr);
        InStr.Position := 100;
        exit('done');
    end;
}
