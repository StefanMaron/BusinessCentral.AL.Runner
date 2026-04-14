// Tests for xRec behavior when modifying records from code (not from pages).
// In BC, xRec from the code path has the SAME values as Rec (new values, not old).
// This is a known BC quirk — xRec is only correctly populated from page triggers.
// This test suite documents and verifies that behavior.
// Uses deterministic PKs: BeforeModify=1, AfterModify=2

table 59965 "XR Source"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

table 59966 "XR EventLog"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; EventType; Text[50]) { }
        field(3; RecName; Text[100]) { }
        field(4; XRecName; Text[100]) { }
        field(5; RecAmount; Integer) { }
        field(6; XRecAmount; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

codeunit 59965 "XR Subscriber"
{
    [EventSubscriber(ObjectType::Table, Database::"XR Source", OnBeforeModifyEvent, '', true, true)]
    local procedure HandleBeforeModify(var Rec: Record "XR Source"; var xRec: Record "XR Source")
    var
        Log: Record "XR EventLog";
    begin
        Log.PK := 1;
        Log.EventType := 'BeforeModify';
        Log.RecName := Rec.Name;
        Log.XRecName := xRec.Name;
        Log.RecAmount := Rec.Amount;
        Log.XRecAmount := xRec.Amount;
        if not Log.Insert() then
            Log.Modify();
    end;

    [EventSubscriber(ObjectType::Table, Database::"XR Source", OnAfterModifyEvent, '', true, true)]
    local procedure HandleAfterModify(var Rec: Record "XR Source"; var xRec: Record "XR Source")
    var
        Log: Record "XR EventLog";
    begin
        Log.PK := 2;
        Log.EventType := 'AfterModify';
        Log.RecName := Rec.Name;
        Log.XRecName := xRec.Name;
        Log.RecAmount := Rec.Amount;
        Log.XRecAmount := xRec.Amount;
        if not Log.Insert() then
            Log.Modify();
    end;
}
