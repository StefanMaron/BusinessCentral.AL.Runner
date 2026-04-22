/// Tests that GetUninitializedObject instances used for event subscriber dispatch
/// and record trigger firing have their null reference-type fields initialized,
/// preventing NullReferenceException in subscriber/trigger bodies.
codeunit 100002 "UIF Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure OnInsertTrigger_SetsFlag_AfterInsert()
    var
        Src: Record "UIF Source";
    begin
        // Positive: the OnInsert trigger sets TriggerRan = true and calls Modify.
        // If GetUninitializedObject fields are not initialized this crashes with NullRef.
        Src.PK := 1;
        Src.Insert(true);  // runTrigger=true

        // TriggerRan is set on Rec inside the trigger body (before Insert completes),
        // but Rec in the trigger IS this record — the in-memory store keeps the mutated state.
        // After Insert, re-fetch to verify the trigger executed without crashing.
        Src.Get(1);
        Assert.IsTrue(Src.TriggerRan, 'OnInsert trigger must have set TriggerRan to true on Rec');
    end;

    [Test]
    procedure OnBeforeInsertEvent_SubscriberSetsFields()
    var
        Src: Record "UIF Source";
    begin
        // Positive: the automatic event subscriber on OnBeforeInsertEvent sets
        // SubscriberRan = true and EventFieldValue = 42 on the Rec parameter.
        // If FireEvent creates the subscriber instance via GetUninitializedObject without
        // initializing null fields, accessing a Record local variable inside the
        // subscriber crashes with NullRef.
        Src.PK := 2;
        Src.Insert();

        Src.Get(2);
        Assert.IsTrue(Src.SubscriberRan, 'OnBeforeInsertEvent subscriber must have set SubscriberRan');
        Assert.AreEqual(42, Src.EventFieldValue, 'OnBeforeInsertEvent subscriber must have set EventFieldValue to 42');
    end;

    [Test]
    procedure OnInsertTrigger_WithoutRunTrigger_DoesNotSetFlag()
    var
        Src: Record "UIF Source";
    begin
        // Negative: Insert without runTrigger=true must NOT fire the AL trigger.
        Src.PK := 3;
        Src.Insert(false);  // runTrigger=false

        Src.Get(3);
        Assert.IsFalse(Src.TriggerRan, 'OnInsert trigger must NOT fire when runTrigger=false');
    end;
}
