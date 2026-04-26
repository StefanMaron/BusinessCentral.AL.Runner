table 59801 "DTE Source"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

table 59802 "DTE Counter"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; InsertCount; Integer) { }
        field(3; ModifyCount; Integer) { }
        field(4; DeleteCount; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

codeunit 59801 "DTE Subscriber"
{
    [EventSubscriber(ObjectType::Table, Database::"DTE Source", OnAfterInsertEvent, '', true, true)]
    local procedure OnAfterInsert(var Rec: Record "DTE Source")
    var
        C: Record "DTE Counter";
    begin
        if not C.Get(1) then begin
            C.PK := 1;
            C.InsertCount := 1;
            C.Insert();
        end else begin
            C.InsertCount += 1;
            C.Modify();
        end;
    end;

    [EventSubscriber(ObjectType::Table, Database::"DTE Source", OnAfterModifyEvent, '', true, true)]
    local procedure OnAfterModify(var Rec: Record "DTE Source"; var xRec: Record "DTE Source")
    var
        C: Record "DTE Counter";
    begin
        if not C.Get(1) then begin
            C.PK := 1;
            C.ModifyCount := 1;
            C.Insert();
        end else begin
            C.ModifyCount += 1;
            C.Modify();
        end;
    end;

    [EventSubscriber(ObjectType::Table, Database::"DTE Source", OnAfterDeleteEvent, '', true, true)]
    local procedure OnAfterDelete(var Rec: Record "DTE Source")
    var
        C: Record "DTE Counter";
    begin
        if not C.Get(1) then begin
            C.PK := 1;
            C.DeleteCount := 1;
            C.Insert();
        end else begin
            C.DeleteCount += 1;
            C.Modify();
        end;
    end;
}
