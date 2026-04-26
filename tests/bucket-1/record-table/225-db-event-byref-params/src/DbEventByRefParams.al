// Tests for implicit DB event subscribers that declare "var" on non-record params
// (e.g. "var RunTrigger: Boolean"). BC compiles these as ByRef<bool> in the
// generated C#, while FireImplicitDbEvent passes a plain bool.
// The runner must wrap the plain value in ByRef<T> before dispatching.

table 59851 "DEBP Source"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

table 59852 "DEBP Log"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; EventType; Text[50]) { }
        field(3; RunTriggerWasTrue; Boolean) { }
        field(4; RecName; Text[100]) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

codeunit 59851 "DEBP Subscriber"
{
    // var RunTrigger: Boolean — BC emits ByRef<bool> for this parameter.
    // Without the fix, InvokeSubscriber crashes with:
    //   ArgumentException: Object of type 'System.Boolean' cannot be converted
    //   to type 'Microsoft.Dynamics.Nav.Runtime.ByRef`1[System.Boolean]'

    [EventSubscriber(ObjectType::Table, Database::"DEBP Source", OnBeforeInsertEvent, '', true, true)]
    local procedure OnBeforeInsert(var Rec: Record "DEBP Source"; var RunTrigger: Boolean)
    var
        Log: Record "DEBP Log";
    begin
        Log.PK := 1;
        Log.EventType := 'BeforeInsert';
        Log.RunTriggerWasTrue := RunTrigger;
        Log.RecName := Rec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"DEBP Source", OnAfterInsertEvent, '', true, true)]
    local procedure OnAfterInsert(var Rec: Record "DEBP Source"; var RunTrigger: Boolean)
    var
        Log: Record "DEBP Log";
    begin
        Log.PK := 2;
        Log.EventType := 'AfterInsert';
        Log.RunTriggerWasTrue := RunTrigger;
        Log.RecName := Rec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"DEBP Source", OnBeforeModifyEvent, '', true, true)]
    local procedure OnBeforeModify(var Rec: Record "DEBP Source"; var xRec: Record "DEBP Source"; var RunTrigger: Boolean)
    var
        Log: Record "DEBP Log";
    begin
        Log.PK := 3;
        Log.EventType := 'BeforeModify';
        Log.RunTriggerWasTrue := RunTrigger;
        Log.RecName := Rec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"DEBP Source", OnAfterModifyEvent, '', true, true)]
    local procedure OnAfterModify(var Rec: Record "DEBP Source"; var xRec: Record "DEBP Source"; var RunTrigger: Boolean)
    var
        Log: Record "DEBP Log";
    begin
        Log.PK := 4;
        Log.EventType := 'AfterModify';
        Log.RunTriggerWasTrue := RunTrigger;
        Log.RecName := Rec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"DEBP Source", OnBeforeDeleteEvent, '', true, true)]
    local procedure OnBeforeDelete(var Rec: Record "DEBP Source"; var RunTrigger: Boolean)
    var
        Log: Record "DEBP Log";
    begin
        Log.PK := 5;
        Log.EventType := 'BeforeDelete';
        Log.RunTriggerWasTrue := RunTrigger;
        Log.RecName := Rec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"DEBP Source", OnAfterDeleteEvent, '', true, true)]
    local procedure OnAfterDelete(var Rec: Record "DEBP Source"; var RunTrigger: Boolean)
    var
        Log: Record "DEBP Log";
    begin
        Log.PK := 6;
        Log.EventType := 'AfterDelete';
        Log.RunTriggerWasTrue := RunTrigger;
        Log.RecName := Rec.Name;
        if not Log.Insert() then
            Log.Modify();
    end;
}
