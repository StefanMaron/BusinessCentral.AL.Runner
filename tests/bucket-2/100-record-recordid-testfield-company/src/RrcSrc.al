/// Helper codeunit exercising Record.RecordId(), Record.TestField(), and Record.CurrentCompany().
/// Two paths are tested:
///   (a) External codeunit calling via "var Rec: Record" parameter — generates rec.Method()
///   (b) Table methods calling bare RecordId()/CurrentCompany() — generates _parent.ALMethod on Record class
codeunit 307801 "RRC Src"
{
    // ── RecordId via var parameter (external call) ────────────────────────────

    /// Return the RecordId of a fresh (uninserted) RRC Table record.
    procedure GetRecordId_Fresh(var Rec: Record "RRC Table"): RecordId
    begin
        exit(Rec.RecordId());
    end;

    /// Return the RecordId of an inserted record.
    procedure GetRecordId_AfterInsert(var Rec: Record "RRC Table"): RecordId
    begin
        Rec."No." := 'REC1';
        Rec."Name" := 'Filled';
        Rec.Insert();
        exit(Rec.RecordId());
    end;

    // ── RecordId via table built-in (direct call on Record class) ─────────────

    /// Table method that calls bare RecordId() — exercises ALRecordId on Record class directly.
    procedure GetBuiltinRecordId(): RecordId
    var
        Tbl: Record "RRC Table";
    begin
        Tbl."No." := 'BRI1';
        Tbl."Name" := 'BuiltinRI';
        Tbl.Insert();
        exit(Tbl.GetOwnRecordIdBuiltin());
    end;

    // ── TestField via var parameter ───────────────────────────────────────────

    /// TestField on a filled Name field — must not throw.
    procedure CheckFilledFieldOk(var Rec: Record "RRC Table")
    begin
        Rec."No." := 'TF1';
        Rec."Name" := 'Filled';
        Rec.Insert();
        Rec.TestField("Name");
    end;

    /// TestField on an empty Name field — must throw with blank-field error.
    procedure CheckEmptyFieldThrows(var Rec: Record "RRC Table")
    begin
        Rec."No." := 'TF2';
        // Name is intentionally left blank
        Rec.Insert();
        Rec.TestField("Name");
    end;

    /// TestField(FieldName, ExpectedValue) — value matches, must not throw.
    procedure CheckMatchingValueOk(var Rec: Record "RRC Table")
    begin
        Rec."No." := 'TF3';
        Rec."Name" := 'Expected';
        Rec.Insert();
        Rec.TestField("Name", 'Expected');
    end;

    /// TestField(FieldName, ExpectedValue) — value mismatch, must throw.
    procedure CheckMismatchValueThrows(var Rec: Record "RRC Table")
    begin
        Rec."No." := 'TF4';
        Rec."Name" := 'Actual';
        Rec.Insert();
        Rec.TestField("Name", 'WrongExpected');
    end;

    // ── CurrentCompany via var parameter ──────────────────────────────────────

    /// Return the current company name for the record (must equal the global CompanyName()).
    procedure GetCurrentCompany(var Rec: Record "RRC Table"): Text
    begin
        exit(Rec.CurrentCompany());
    end;

    // ── CurrentCompany via table built-in (direct call on Record class) ───────

    /// Table method that calls bare CurrentCompany() — exercises ALCurrentCompany on Record class.
    procedure GetBuiltinCurrentCompany(): Text
    var
        Tbl: Record "RRC Table";
    begin
        exit(Tbl.GetOwnCurrentCompanyBuiltin());
    end;
}
