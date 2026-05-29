/// <summary>
/// Subscribes to OnAfterValidateEvent of "Mut Probe ESM"."Trigger Field" and
/// mutates the record's "Target Field" through the by-reference var Rec
/// parameter. In real BC this write propagates to the record being validated.
///
/// Uses the FULL field-validate event signature (var Rec, var xRec,
/// CurrFieldNo) to exercise parameter-slot binding, and mutates through a
/// nested by-ref local procedure — matching the reported RS pattern.
/// </summary>
codeunit 60260 "Mut Subscriber ESM"
{
    [EventSubscriber(ObjectType::Table, Database::"Mut Probe ESM", OnAfterValidateEvent, 'Trigger Field', false, false)]
    local procedure TriggerField_OnAfterValidate(var Rec: Record "Mut Probe ESM"; var xRec: Record "Mut Probe ESM"; CurrFieldNo: Integer)
    begin
        UpdateTargetField(Rec, Rec."Trigger Field");
    end;

    local procedure UpdateTargetField(var ProbeRec: Record "Mut Probe ESM"; SourceValue: Code[20])
    begin
        ProbeRec."Target Field" := CopyStr('MUTATED:' + SourceValue, 1, MaxStrLen(ProbeRec."Target Field"));
    end;
}
