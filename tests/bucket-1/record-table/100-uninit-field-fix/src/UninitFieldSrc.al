/// Table with global var section and OnInsert trigger that uses the global.
/// Variables declared in the table's global var section become backing fields
/// on the generated Record class alongside Rec and xRec. When TryFireRecordTrigger
/// calls GetUninitializedObject, only Rec/xRec are explicitly set — the global
/// Helper field is null. The trigger body accesses Helper.PK which causes NullRef.
table 100001 "UIF Source"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; TriggerRan; Boolean) { }
        field(3; SubscriberRan; Boolean) { }
        field(4; EventFieldValue; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }

    var
        // This global variable compiles to a backing field on Record100001.
        // TryFireRecordTrigger sets <Rec>k__BackingField and <xRec>k__BackingField
        // but NOT <Helper>k__BackingField. Without the fix, Helper is null and
        // accessing Helper.PK inside the trigger throws NullReferenceException.
        Helper: Record "UIF Source";

    trigger OnInsert()
    begin
        // Access the table-global variable — null without the fix.
        Rec.TriggerRan := (Helper.PK = 0);
    end;
}

/// Counter table used by the event subscriber.
table 100002 "UIF Counter"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Hits; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

/// Codeunit with a codeunit-level global Record variable AND an event subscriber.
/// InitializeComponent initializes GlobalCounter, so this case already works.
/// This test proves it continues to work after the fix is applied.
codeunit 100001 "UIF Subscriber"
{
    var
        GlobalCounter: Record "UIF Counter";

    [EventSubscriber(ObjectType::Table, Database::"UIF Source", OnBeforeInsertEvent, '', true, true)]
    local procedure OnBeforeInsert(var Rec: Record "UIF Source"; RunTrigger: Boolean)
    begin
        // GlobalCounter is initialized by InitializeComponent — must not be null.
        if GlobalCounter.Get(999) then
            Rec.EventFieldValue := GlobalCounter.Hits
        else begin
            Rec.SubscriberRan := true;
            Rec.EventFieldValue := 42;
        end;
    end;
}
