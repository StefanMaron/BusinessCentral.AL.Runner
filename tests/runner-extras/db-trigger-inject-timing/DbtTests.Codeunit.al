/// Regression for issue #2197. See app.json for the full mechanism writeup: the bundle-level
/// bulk subscriber-injection passes (EventSubscriberPatches.DoInject, run once per bundle and
/// once per test codeunit) only ever see NCLMetaTables that are ALREADY built -- a precompiled
/// Base App table first touched mid-codeunit (this bundle's first and only Job/"Job Task"
/// touch, guaranteed by this being the only runner-extras suite referencing either table) used
/// to miss every pass that could have wired its subscriber, leaving it silently inert.
codeunit 65252 "Dbt Trigger Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Dbt Assert";

    [Test]
    procedure JobFirstTouchInThisCodeunit_DeleteRaisesOnBeforeDeleteEvent()
    var
        Job: Record Job;
    begin
        // Job's NCLMetaTable does not exist anywhere in this process before this line runs --
        // it is built LAZILY, right here, mid-codeunit. Before the #2197 fix, the subscriber in
        // "Dbt Trigger Sub" (codeunit 65251) never got wired: the bulk injection pass that ran
        // before this test codeunit started found no Job metatable yet, and no later codeunit
        // in this bundle ever touches Job again to trigger a retry.
        Job."No." := 'DBT-JOB-1';
        Job.Insert(false);
        Commit();

        asserterror Job.Delete(true);
        Assert.ExpectedError('JOB OnBeforeDeleteEvent FIRED');
    end;

    [Test]
    procedure UnrelatedTableWithNoSubscriber_DeleteDoesNotRaise()
    var
        JobTask: Record "Job Task";
    begin
        // Negative: "Job Task" has no OnBeforeDeleteEvent subscriber anywhere in this bundle.
        // This is what rules out a no-op fix that makes every Delete on a lazily-built table
        // raise, rather than one that wires the specific subscribed (table, event) pair.
        JobTask."Job No." := 'DBT-JOB-2';
        JobTask."Job Task No." := 'DBT-TASK-1';
        JobTask.Insert(false);
        Commit();

        JobTask.Delete(true);
        Assert.IsFalse(JobTask.Get('DBT-JOB-2', 'DBT-TASK-1'), 'the row must actually be deleted -- Delete(true) must not have been intercepted by some unrelated error path');
    end;
}
