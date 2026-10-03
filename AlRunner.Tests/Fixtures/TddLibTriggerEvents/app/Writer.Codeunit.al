/// <summary>
/// #5286: procedures that start a trigger or a database event of the app's tables. The subscribers are in the
/// library and the test bundle, which are compiled after this one, so only the edges recorded here connect a
/// test calling one of these to a stub.
/// </summary>
codeunit 72404 "TLib Writer"
{
    procedure Write(Id: Code[20])
    var
        Rec: Record "TLib Rec";
    begin
        Rec.PK := Id;
        Rec.Insert();
    end;

    procedure ModifyRec(Id: Code[20])
    var
        Rec: Record "TLib Rec";
    begin
        Rec.Get(Id);
        Rec.Qty := 1;
        Rec.Modify(true);
    end;

    procedure Remove(Id: Code[20])
    var
        Rec: Record "TLib Rec";
    begin
        Rec.Get(Id);
        Rec.Delete();
    end;

    procedure WriteQuiet(Id: Code[20])
    var
        Rec: Record "TLib Quiet Rec";
    begin
        Rec.PK := Id;
        Rec.Insert(true);
    end;

    procedure RunById(CodeunitId: Integer)
    begin
        Codeunit.Run(CodeunitId);
    end;

    procedure InsertAny(TableId: Integer; Id: Code[20])
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(TableId);
        RecRef.Field(1).Value := Id;
        RecRef.Insert(true);
    end;
}
