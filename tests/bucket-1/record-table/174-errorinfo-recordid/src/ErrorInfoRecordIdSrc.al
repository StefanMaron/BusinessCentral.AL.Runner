/// Helper codeunit exercising ErrorInfo.RecordId getter.
///
/// Note on scope: the setter path (`ei.RecordId := item.RecordId()`) does not
/// persist through the BC runtime standalone — NavALErrorInfo.ALRecordId is
/// tied to an internal NavRecord reference that requires a live session, so
/// the assigned value is not observable via the getter. The getter ITSELF
/// works (returns the default RecordId on a fresh ErrorInfo); this suite
/// covers that slice. Filing a follow-up would cover the setter once the
/// session-dependency can be lifted.
codeunit 59990 "EIR Src"
{
    procedure FreshRecordId_TableNo(): Integer
    var
        ei: ErrorInfo;
    begin
        exit(ei.RecordId.TableNo);
    end;

    procedure FreshRecordId_MatchesDefault(): Boolean
    var
        ei: ErrorInfo;
        emptyId: RecordId;
    begin
        exit(Format(ei.RecordId) = Format(emptyId));
    end;

    procedure ReadRecordId_DoesNotThrow(): Boolean
    var
        ei: ErrorInfo;
        ri: RecordId;
    begin
        ri := ei.RecordId;
        exit(true);
    end;

    procedure ReadRecordIdTwice_Stable(): Boolean
    var
        ei: ErrorInfo;
        a: RecordId;
        b: RecordId;
    begin
        a := ei.RecordId;
        b := ei.RecordId;
        exit(Format(a) = Format(b));
    end;
}
