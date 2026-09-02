/// Subscriber-only codeunit for issue #2197 -- table-level trigger subscriber on a precompiled
/// Base App table (Job) that no other object in this bundle touches before the test codeunit
/// runs, so Job's NCLMetaTable is guaranteed to be built LAZILY, mid test-codeunit.
codeunit 65251 "Dbt Trigger Sub"
{
    [EventSubscriber(ObjectType::Table, Database::Job, 'OnBeforeDeleteEvent', '', false, false)]
    local procedure ProbeJobDelete(var Rec: Record Job; RunTrigger: Boolean)
    begin
        Error('JOB OnBeforeDeleteEvent FIRED');
    end;
}
