table 50114 "Stream Test Data"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Content"; Blob) { }
    }

    keys
    {
        key(PK; "Entry No.") { }
    }
}

codeunit 50114 "InStream Methods"
{
    procedure TestLength(): Integer
    var
        Rec: Record "Stream Test Data";
        InStr: InStream;
        OutStr: OutStream;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateOutStream(OutStr);
        OutStr.WriteText('Hello World');
        Rec.Content.CreateInStream(InStr);
        exit(InStr.Length);
    end;

    procedure TestPosition(): Integer
    var
        Rec: Record "Stream Test Data";
        InStr: InStream;
        OutStr: OutStream;
        Dummy: Text;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateOutStream(OutStr);
        OutStr.WriteText('Hello World');
        Rec.Content.CreateInStream(InStr);
        InStr.ReadText(Dummy, 5);
        exit(InStr.Position);
    end;

    procedure TestResetPosition(): Text
    var
        Rec: Record "Stream Test Data";
        InStr: InStream;
        OutStr: OutStream;
        Dummy: Text;
        Result: Text;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateOutStream(OutStr);
        OutStr.WriteText('Hello World');
        Rec.Content.CreateInStream(InStr);
        InStr.ReadText(Dummy, 5);
        InStr.ResetPosition();
        InStr.ReadText(Result);
        exit(Result);
    end;
}

