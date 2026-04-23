/// Simple table for Clear Methods tests — provides a record type with an
/// integer field to exercise Clear(RecordArray[i]).
table 302010 "CMH Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Value; Integer) { }
        field(3; Data; Blob) { }
    }
    keys { key(PK; Id) { } }
}

codeunit 302001 "Clear Methods Helper"
{
    procedure GetOutStream(var OS: OutStream)
    var
        Rec: Record "CMH Record" temporary;
    begin
        Rec.Data.CreateOutStream(OS);
    end;

    procedure ClearOutStream(var OS: OutStream)
    begin
        Clear(OS);
    end;

    procedure ClearFile(var F: File)
    begin
        Clear(F);
    end;

    procedure ClearRecordArrayElement(var RecArr: array[3] of Record "CMH Record"; Idx: Integer)
    begin
        Clear(RecArr[Idx]);
    end;

    procedure SetRecordArrayField(var RecArr: array[3] of Record "CMH Record"; Idx: Integer; Value: Integer)
    begin
        RecArr[Idx].Value := Value;
    end;

    procedure GetRecordArrayField(var RecArr: array[3] of Record "CMH Record"; Idx: Integer): Integer
    begin
        exit(RecArr[Idx].Value);
    end;
}
