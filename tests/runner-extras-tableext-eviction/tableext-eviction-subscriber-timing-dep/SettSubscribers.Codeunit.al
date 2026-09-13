/// The subscriber side of the #2510 regression: EventSubscriberPatches._injectedSubscriberMethods
/// is keyed by the subscriber MethodInfo only, with no per-table index, so nothing purges it when
/// RecordPatches.EvictCachedMetaTableForBaseTable rebuilds "Sett T"'s NCLMetaTable after the sibling
/// "test" app's tableextension merges in. Both a table-level ordinal subscriber (OnBeforeInsertEvent,
/// dispatched via EventSubscriberPatches.InjectTriggerSubsForTable / _byKey) and a field-scoped
/// validate subscriber (OnAfterValidateEvent on "Val", dispatched via InjectValidateSubsForTable /
/// _validateSubs) are covered here since #2506 fixed only the table's OWN compiled field triggers,
/// not either of these subscriber-injection paths. See app.json / the sibling "test" app for the
/// eviction-forcing half.
codeunit 65521 "Sett Subscribers"
{
    [EventSubscriber(ObjectType::Table, Database::"Sett T", 'OnBeforeInsertEvent', '', false, false)]
    local procedure OnBeforeInsertSettT(var Rec: Record "Sett T")
    begin
        Rec.InsertFlag := true;
    end;

    [EventSubscriber(ObjectType::Table, Database::"Sett T", 'OnAfterValidateEvent', 'Val', false, false)]
    local procedure OnAfterValidateVal(var Rec: Record "Sett T")
    begin
        Rec.Computed := Rec.Val * 3;
    end;
}
