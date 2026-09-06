// The refusal, raised where Microsoft's own one is raised: in a subscriber to the table's
// OnBeforeDeleteEvent, NOT in the page control's OnValidate body. Nothing on the page knows
// this exists; the only thing connecting it to the control write is the platform's table-event
// dispatch under Delete(true).
codeunit 70602 "TSR Guard"
{
    var
        GuardedRowErr: Label 'TSR row %1 is guarded and cannot be deleted', Comment = '%1 = the row No.';

    [EventSubscriber(ObjectType::Table, Database::"TSR Row", 'OnBeforeDeleteEvent', '', false, false)]
    local procedure RefuseGuardedRow(var Rec: Record "TSR Row"; RunTrigger: Boolean)
    begin
        if not RunTrigger then
            exit;
        if Rec.Guarded then
            Error(GuardedRowErr, Rec."No.");
    end;

    // The expected text, so the test asserts against the label the subscriber actually raises
    // rather than a copy of it that could drift.
    procedure ExpectedMessage(No: Code[20]): Text
    begin
        exit(StrSubstNo(GuardedRowErr, No));
    end;
}
