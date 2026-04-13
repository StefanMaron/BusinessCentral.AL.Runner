codeunit 56880 "RF Find Probe"
{
    procedure FindRecordViaRecRef(TableId: Integer): Boolean
    var
        RecRef: RecordRef;
        Found: Boolean;
    begin
        RecRef.Open(TableId);
        Found := RecRef.Find();
        RecRef.Close();
        exit(Found);
    end;

    procedure FindFirstViaRecRef(TableId: Integer): Boolean
    var
        RecRef: RecordRef;
        Found: Boolean;
    begin
        RecRef.Open(TableId);
        Found := RecRef.FindFirst();
        RecRef.Close();
        exit(Found);
    end;

    procedure FindWithWhichViaRecRef(TableId: Integer; Which: Text[1]): Boolean
    var
        RecRef: RecordRef;
        Found: Boolean;
    begin
        RecRef.Open(TableId);
        Found := RecRef.Find(Which);
        RecRef.Close();
        exit(Found);
    end;
}
