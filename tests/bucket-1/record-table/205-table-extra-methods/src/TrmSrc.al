/// Helper codeunit for Table extra methods:
/// ChangeCompany, GetAscending, GetBySystemId, LoadFields, ReadConsistency,
/// ReadIsolation, SetLoadFields, SetBaseLoadFields, SetPermissionFilter,
/// Truncate, Relation, Consistent, SecurityFiltering.
codeunit 97701 "TRM Src"
{
    // ── GetAscending ──────────────────────────────────────────────────────────

    procedure GetAscendingForField(var Rec: Record "TRM Table"): Boolean
    begin
        exit(Rec.GetAscending("No."));
    end;

    // ── ChangeCompany ─────────────────────────────────────────────────────────

    procedure DoChangeCompany(var Rec: Record "TRM Table"; Company: Text): Boolean
    begin
        exit(Rec.ChangeCompany(Company));
    end;

    // ── GetBySystemId ─────────────────────────────────────────────────────────

    procedure DoGetBySystemId(var Rec: Record "TRM Table"; SysId: Guid): Boolean
    begin
        exit(Rec.GetBySystemId(SysId));
    end;

    // ── LoadFields ────────────────────────────────────────────────────────────

    procedure DoLoadFields(var Rec: Record "TRM Table")
    begin
        Rec.LoadFields(Rec."No.", Rec.Name);
    end;

    // ── SetLoadFields ─────────────────────────────────────────────────────────

    procedure DoSetLoadFields(var Rec: Record "TRM Table")
    begin
        Rec.SetLoadFields(Rec."No.");
    end;

    // ── SetBaseLoadFields ─────────────────────────────────────────────────────

    procedure DoSetBaseLoadFields(var Rec: Record "TRM Table")
    begin
        Rec.SetBaseLoadFields();
    end;

    // ── ReadConsistency ───────────────────────────────────────────────────────

    procedure DoReadConsistency(var Rec: Record "TRM Table"): Boolean
    begin
        exit(Rec.ReadConsistency());
    end;

    // ── SetPermissionFilter ───────────────────────────────────────────────────

    procedure DoSetPermissionFilter(var Rec: Record "TRM Table")
    begin
        Rec.SetPermissionFilter();
    end;

    // ── Truncate ──────────────────────────────────────────────────────────────

    procedure InsertAndTruncate(var Rec: Record "TRM Table"): Integer
    var
        CountBefore: Integer;
    begin
        Rec."No." := 'A';
        Rec.Insert();
        Rec."No." := 'B';
        Rec.Insert();
        CountBefore := Rec.Count();
        Rec.Truncate();
        exit(Rec.Count());
    end;

    // ── Relation ──────────────────────────────────────────────────────────────

    procedure GetRelation(var Rec: Record "TRM Table"): Integer
    begin
        exit(Rec.Relation("No."));
    end;

    // ── ReadIsolation ─────────────────────────────────────────────────────────

    procedure SetReadIsolation(var Rec: Record "TRM Table")
    begin
        Rec.ReadIsolation := IsolationLevel::ReadUncommitted;
    end;

    // ── Consistent ────────────────────────────────────────────────────────────

    procedure DoConsistent(var Rec: Record "TRM Table")
    begin
        Rec.Consistent(true);
    end;

    // ── SecurityFiltering ─────────────────────────────────────────────────────

    procedure GetSecurityFiltering(var Rec: Record "TRM Table"): SecurityFilter
    begin
        exit(Rec.SecurityFiltering());
    end;

    procedure SetSecurityFiltering(var Rec: Record "TRM Table"; Filter: SecurityFilter)
    begin
        Rec.SecurityFiltering(Filter);
    end;
}
