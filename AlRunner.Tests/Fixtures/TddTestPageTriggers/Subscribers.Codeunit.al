codeunit 72614 "TP Subscribers"
{
    [EventSubscriber(ObjectType::Table, Database::"TP Evt", 'OnAfterInsertEvent', '', false, false)]
    local procedure OnAfterInsert(var Rec: Record "TP Evt"; RunTrigger: Boolean)
    var T: Codeunit "TP Target"; R: Integer;
    begin
        R := T.MEvtInsert(1);
    end;

    [EventSubscriber(ObjectType::Table, Database::"TP Evt", 'OnAfterModifyEvent', '', false, false)]
    local procedure OnAfterModify(var Rec: Record "TP Evt"; var xRec: Record "TP Evt"; RunTrigger: Boolean)
    var T: Codeunit "TP Target"; R: Integer;
    begin
        R := T.MEvtModify(1);
    end;

    [EventSubscriber(ObjectType::Table, Database::"TP Evt", 'OnAfterRenameEvent', '', false, false)]
    local procedure OnAfterRename(var Rec: Record "TP Evt"; var xRec: Record "TP Evt"; RunTrigger: Boolean)
    var T: Codeunit "TP Target"; R: Integer;
    begin
        R := T.MEvtRename(1);
    end;

    [EventSubscriber(ObjectType::Table, Database::"TP Evt", 'OnAfterValidateEvent', 'V', false, false)]
    local procedure OnAfterValidateV(var Rec: Record "TP Evt"; var xRec: Record "TP Evt"; CurrFieldNo: Integer)
    var T: Codeunit "TP Target"; R: Integer;
    begin
        R := T.MEvtValidate(1);
    end;

    // No TestPage operation deletes a record: nothing reaches this one.
    [EventSubscriber(ObjectType::Table, Database::"TP Evt", 'OnAfterDeleteEvent', '', false, false)]
    local procedure OnAfterDelete(var Rec: Record "TP Evt"; RunTrigger: Boolean)
    var T: Codeunit "TP Target"; R: Integer;
    begin
        R := T.MEvtDelete(1);
    end;
}
