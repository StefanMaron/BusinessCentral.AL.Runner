// Tests for OnBefore* DB trigger events: OnBeforeInsertEvent, OnBeforeModifyEvent, OnBeforeDeleteEvent.
// These fire BEFORE the database operation and before the table trigger.
// Uses deterministic PKs per event type to avoid SingleInstance dependency.

table 59960 "BDE Source"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

table 59961 "BDE EventLog"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; EventType; Text[50]) { }
        field(3; RecName; Text[100]) { }
        field(4; XRecName; Text[100]) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

codeunit 59960 "BDE Subscriber"
{
    // Deterministic PKs: BeforeInsert=1, AfterInsert=2, BeforeModify=3,
    // AfterModify=4, BeforeDelete=5, AfterDelete=6

    [EventSubscriber(ObjectType::Table, Database::"BDE Source", OnBeforeInsertEvent, '', true, true)]
    local procedure HandleBeforeInsert(var Rec: Record "BDE Source")
    var
        Log: Record "BDE EventLog";
    begin
        Log.PK := 1;
        Log.EventType := 'BeforeInsert';
        Log.RecName := Rec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"BDE Source", OnAfterInsertEvent, '', true, true)]
    local procedure HandleAfterInsert(var Rec: Record "BDE Source")
    var
        Log: Record "BDE EventLog";
    begin
        Log.PK := 2;
        Log.EventType := 'AfterInsert';
        Log.RecName := Rec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"BDE Source", OnBeforeModifyEvent, '', true, true)]
    local procedure HandleBeforeModify(var Rec: Record "BDE Source"; var xRec: Record "BDE Source")
    var
        Log: Record "BDE EventLog";
    begin
        Log.PK := 3;
        Log.EventType := 'BeforeModify';
        Log.RecName := Rec.Name;
        Log.XRecName := xRec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"BDE Source", OnAfterModifyEvent, '', true, true)]
    local procedure HandleAfterModify(var Rec: Record "BDE Source"; var xRec: Record "BDE Source")
    var
        Log: Record "BDE EventLog";
    begin
        Log.PK := 4;
        Log.EventType := 'AfterModify';
        Log.RecName := Rec.Name;
        Log.XRecName := xRec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"BDE Source", OnBeforeDeleteEvent, '', true, true)]
    local procedure HandleBeforeDelete(var Rec: Record "BDE Source")
    var
        Log: Record "BDE EventLog";
    begin
        Log.PK := 5;
        Log.EventType := 'BeforeDelete';
        Log.RecName := Rec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"BDE Source", OnAfterDeleteEvent, '', true, true)]
    local procedure HandleAfterDelete(var Rec: Record "BDE Source")
    var
        Log: Record "BDE EventLog";
    begin
        Log.PK := 6;
        Log.EventType := 'AfterDelete';
        Log.RecName := Rec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;
}
