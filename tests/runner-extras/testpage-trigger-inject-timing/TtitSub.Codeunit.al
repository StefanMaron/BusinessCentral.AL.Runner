/// Subscriber-only codeunit for issue #2411 -- table-level trigger subscriber on a precompiled
/// Base App table (Warehouse Employee) that no other object in this bundle touches before a
/// directly-opened TestPage does, so Warehouse Employee's NCLMetaTable is guaranteed to be
/// built LAZILY, by TestPageFactory.TryBuildBlankRecord, rather than by a bare
/// `Record "Warehouse Employee"` variable.
///
/// IMPORTANT: this test does NOT discriminate the #2411 fix in TestPageFactory
/// .TryBuildBlankRecord -- it is GREEN with that fix removed too. See app.json for the full,
/// measured explanation: BC's own SetSourceTable/NewRecordAsync machinery, which every live
/// TestPage/Page-variable construction with a real compiled page object goes through, already
/// wires this subscriber via one of #2412's three ALREADY-fixed sites (NCLMetaTable
/// .CreateObjectInstance, building the table's xRec) before Insert ever dispatches. It stays
/// in the suite as an end-to-end regression/contract test -- "a table-level trigger subscriber
/// on a table first touched via a TestPage does eventually fire" -- not as proof of this
/// specific diff.
codeunit 65261 "Ttit Trigger Sub"
{
    [EventSubscriber(ObjectType::Table, Database::"Warehouse Employee", 'OnBeforeInsertEvent', '', false, false)]
    local procedure ProbeWarehouseEmployeeInsert(var Rec: Record "Warehouse Employee"; RunTrigger: Boolean)
    begin
        Error('WAREHOUSE EMPLOYEE OnBeforeInsertEvent FIRED');
    end;
}
