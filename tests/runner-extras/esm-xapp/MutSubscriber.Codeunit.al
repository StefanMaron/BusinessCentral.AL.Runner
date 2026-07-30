/// <summary>
/// CROSS-APP subscriber: this codeunit lives in the main app but subscribes to
/// the OnAfterValidateEvent of a field on "Mut Probe XESM", a table defined in
/// the sibling dependency app. Mutates the record through the by-reference
/// var Rec parameter (via a nested var local proc, matching the RS pattern).
/// </summary>
codeunit 63360 "Mut Subscriber XESM"
{
    [EventSubscriber(ObjectType::Table, Database::"Mut Probe XESM", OnAfterValidateEvent, 'Trigger Field', false, false)]
    local procedure TriggerField_OnAfterValidate(var Rec: Record "Mut Probe XESM"; var xRec: Record "Mut Probe XESM"; CurrFieldNo: Integer)
    begin
        UpdateTargetField(Rec, Rec."Trigger Field");
    end;

    local procedure UpdateTargetField(var ProbeRec: Record "Mut Probe XESM"; SourceValue: Code[20])
    begin
        ProbeRec."Target Field" := CopyStr('MUTATED:' + SourceValue, 1, MaxStrLen(ProbeRec."Target Field"));
    end;
}
