// The platform events a page publishes (handlers/TestPageTriggerEvents.al in the corpus).
codeunit 72806 "PT Subs"
{
    [EventSubscriber(ObjectType::Page, Page::"PT Card", 'OnOpenPageEvent', '', false, false)]
    local procedure OnOpen(var Rec: Record "PT Rec") var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtOpen(1); end;

    [EventSubscriber(ObjectType::Page, Page::"PT Card", 'OnClosePageEvent', '', false, false)]
    local procedure OnClose(var Rec: Record "PT Rec") var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtClose(1); end;

    [EventSubscriber(ObjectType::Page, Page::"PT Card", 'OnAfterGetRecordEvent', '', false, false)]
    local procedure OnAfterGet(var Rec: Record "PT Rec") var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtAfterGet(1); end;

    [EventSubscriber(ObjectType::Page, Page::"PT Card", 'OnModifyRecordEvent', '', false, false)]
    local procedure OnModify(var Rec: Record "PT Rec"; var xRec: Record "PT Rec"; var AllowModify: Boolean) var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtModify(1); end;

    [EventSubscriber(ObjectType::Page, Page::"PT Card", 'OnQueryClosePageEvent', '', false, false)]
    local procedure OnQuery(var Rec: Record "PT Rec"; var AllowClose: Boolean) var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtQuery(1); end;

    [EventSubscriber(ObjectType::Page, Page::"PT Card", 'OnAfterGetCurrRecordEvent', '', false, false)]
    local procedure OnAfterGetCurr(var Rec: Record "PT Rec") var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtAfterGetCurr(1); end;

    [EventSubscriber(ObjectType::Page, Page::"PT Card", 'OnNewRecordEvent', '', false, false)]
    local procedure OnNew(var Rec: Record "PT Rec"; BelowxRec: Boolean; var xRec: Record "PT Rec") var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtNew(1); end;

    [EventSubscriber(ObjectType::Page, Page::"PT Card", 'OnInsertRecordEvent', '', false, false)]
    local procedure OnInsert(var Rec: Record "PT Rec"; BelowxRec: Boolean; var xRec: Record "PT Rec"; var AllowInsert: Boolean) var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtInsert(1); end;

    [EventSubscriber(ObjectType::Page, Page::"PT Card", 'OnDeleteRecordEvent', '', false, false)]
    local procedure OnDelete(var Rec: Record "PT Rec"; var AllowDelete: Boolean) var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtDelete(1); end;

    [EventSubscriber(ObjectType::Page, Page::"PT Quiet Card", 'OnOpenPageEvent', '', false, false)]
    local procedure OnQuietOpen(var Rec: Record "PT Quiet") var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtQuietOpen(1); end;
}
