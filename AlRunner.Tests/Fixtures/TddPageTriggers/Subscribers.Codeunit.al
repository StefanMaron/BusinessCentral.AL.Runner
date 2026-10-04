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

    [EventSubscriber(ObjectType::Page, Page::"PT Quiet Card", 'OnOpenPageEvent', '', false, false)]
    local procedure OnQuietOpen(var Rec: Record "PT Quiet") var T: Codeunit "PT Target"; R: Integer; begin R := T.MEvtQuietOpen(1); end;
}
