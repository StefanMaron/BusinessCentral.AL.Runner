table 56800 "Isolation Probe"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Data; Blob) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

codeunit 56800 "Isolation Probe"
{
    procedure SetRecordReadIsolation()
    var
        Rec: Record "Isolation Probe";
    begin
        Rec.ReadIsolation := IsolationLevel::ReadUncommitted;
    end;

    procedure SetRecRefReadIsolation()
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"Isolation Probe");
        RecRef.ReadIsolation := IsolationLevel::ReadUncommitted;
        RecRef.Close();
    end;

    procedure DuplicateRecRef(): Integer
    var
        RecRef: RecordRef;
        RecRef2: RecordRef;
        FRef: FieldRef;
    begin
        RecRef.Open(Database::"Isolation Probe");

        // Insert a record via RecRef
        FRef := RecRef.Field(1);
        FRef.Value := 10;
        FRef := RecRef.Field(2);
        FRef.Value := 'Hello';
        RecRef.Insert();

        // Duplicate
        RecRef2 := RecRef.Duplicate();

        // The duplicate should see the same table
        exit(RecRef2.Count());
    end;

    procedure AssignInStream()
    var
        Rec: Record "Isolation Probe";
        InStr1: InStream;
        InStr2: InStream;
        OutStr: OutStream;
        ReadTxt: Text;
    begin
        Rec."Entry No." := 1;
        Rec.Data.CreateOutStream(OutStr);
        OutStr.WriteText('StreamCopy');
        Rec.Insert();

        Rec.CalcFields(Data);
        Rec.Data.CreateInStream(InStr1);
        InStr2 := InStr1;
        InStr2.ReadText(ReadTxt);
        if ReadTxt <> 'StreamCopy' then
            Error('InStream assign failed: expected StreamCopy, got %1', ReadTxt);
    end;
}
