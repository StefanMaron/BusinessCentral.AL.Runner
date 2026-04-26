/// In-suite table used by the table-metadata-stubs test.
table 107000 "TMI Record"
{
    Caption = 'TMI Record';

    fields
    {
        field(1; Id; Integer) { Caption = 'Id'; }
        field(2; Name; Text[50]) { Caption = 'Name'; }
        field(3; Amount; Decimal) { Caption = 'Amount'; }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// Helper codeunit exercising Table record metadata and introspection methods.
codeunit 107000 "TMI Src"
{
    // ── FieldCaption ────────────────────────────────────────────────────────────

    procedure FieldCaptionId(var Rec: Record "TMI Record"): Text
    begin
        exit(Rec.FieldCaption(Id));
    end;

    procedure FieldCaptionName(var Rec: Record "TMI Record"): Text
    begin
        exit(Rec.FieldCaption(Name));
    end;

    procedure FieldCaptionAmount(var Rec: Record "TMI Record"): Text
    begin
        exit(Rec.FieldCaption(Amount));
    end;

    // ── FieldName ───────────────────────────────────────────────────────────────

    procedure FieldNameId(var Rec: Record "TMI Record"): Text
    begin
        exit(Rec.FieldName(Id));
    end;

    procedure FieldNameName(var Rec: Record "TMI Record"): Text
    begin
        exit(Rec.FieldName(Name));
    end;

    // ── FieldActive ─────────────────────────────────────────────────────────────

    procedure IsFieldIdActive(var Rec: Record "TMI Record"): Boolean
    begin
        exit(Rec.FieldActive(Id));
    end;

    procedure IsFieldNameActive(var Rec: Record "TMI Record"): Boolean
    begin
        exit(Rec.FieldActive(Name));
    end;

    // ── FieldError ──────────────────────────────────────────────────────────────

    procedure TriggerFieldError(var Rec: Record "TMI Record")
    begin
        Rec.FieldError(Name);
    end;

    procedure TriggerFieldErrorWithMessage(var Rec: Record "TMI Record"; Msg: Text)
    begin
        Rec.FieldError(Name, Msg);
    end;

    // ── TableCaption ────────────────────────────────────────────────────────────

    procedure GetTableCaption(var Rec: Record "TMI Record"): Text
    begin
        exit(Rec.TableCaption());
    end;

    // ── TableName ───────────────────────────────────────────────────────────────

    procedure GetTableName(var Rec: Record "TMI Record"): Text
    begin
        exit(Rec.TableName());
    end;

    // ── CurrentKey ──────────────────────────────────────────────────────────────

    procedure GetCurrentKey(var Rec: Record "TMI Record"): Text
    begin
        exit(Rec.CurrentKey());
    end;

    // ── CurrentCompany ──────────────────────────────────────────────────────────

    procedure GetCurrentCompany(var Rec: Record "TMI Record"): Text
    begin
        exit(Rec.CurrentCompany());
    end;

    // ── IsTemporary ─────────────────────────────────────────────────────────────

    procedure CheckIsTemporary(var Rec: Record "TMI Record"): Boolean
    begin
        exit(Rec.IsTemporary());
    end;

    procedure CheckTempIsTemporary(var Rec: Record "TMI Record" temporary): Boolean
    begin
        exit(Rec.IsTemporary());
    end;

    // ── ReadPermission ──────────────────────────────────────────────────────────

    procedure HasReadPermission(var Rec: Record "TMI Record"): Boolean
    begin
        exit(Rec.ReadPermission());
    end;

    // ── WritePermission ─────────────────────────────────────────────────────────

    procedure HasWritePermission(var Rec: Record "TMI Record"): Boolean
    begin
        exit(Rec.WritePermission());
    end;

    // ── RecordLevelLocking ──────────────────────────────────────────────────────

    procedure GetRecordLevelLocking(var Rec: Record "TMI Record"): Boolean
    begin
        exit(Rec.RecordLevelLocking());
    end;

    // ── RecordId ────────────────────────────────────────────────────────────────

    procedure GetRecordIdDoesNotCrash(var Rec: Record "TMI Record"): Boolean
    var
        RId: RecordId;
    begin
        RId := Rec.RecordId();
        exit(true); // just prove it doesn't crash
    end;
}
