/// Tests for Table extra methods:
/// ChangeCompany, GetAscending, GetBySystemId, LoadFields, ReadConsistency,
/// ReadIsolation, SetLoadFields, SetBaseLoadFields, SetPermissionFilter,
/// Truncate, Relation, Consistent, SecurityFiltering.
codeunit 97702 "TRM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "TRM Src";

    // ── GetAscending ──────────────────────────────────────────────────────────

    [Test]
    procedure GetAscending_DefaultTrue()
    var
        Rec: Record "TRM Table";
    begin
        Assert.IsTrue(H.GetAscendingForField(Rec), 'GetAscending must return true by default');
    end;

    // ── ChangeCompany ─────────────────────────────────────────────────────────

    [Test]
    procedure ChangeCompany_NoThrow()
    var
        Rec: Record "TRM Table";
        Result: Boolean;
    begin
        Result := H.DoChangeCompany(Rec, 'CRONUS');
        Assert.IsTrue(true, 'ChangeCompany must not throw');
    end;

    // ── GetBySystemId ─────────────────────────────────────────────────────────

    [Test]
    procedure GetBySystemId_NullGuid_ReturnsFalse()
    var
        Rec: Record "TRM Table";
        NullId: Guid;
    begin
        Assert.IsFalse(H.DoGetBySystemId(Rec, NullId), 'GetBySystemId with null guid must return false');
    end;

    [Test]
    procedure GetBySystemId_InsertedRecord_ReturnsTrue()
    var
        Rec: Record "TRM Table";
        SysId: Guid;
    begin
        Rec."No." := 'SYSID1';
        Rec.Insert();
        SysId := Rec.SystemId;
        Assert.IsTrue(H.DoGetBySystemId(Rec, SysId), 'GetBySystemId must find inserted record');
    end;

    // ── LoadFields ────────────────────────────────────────────────────────────

    [Test]
    procedure LoadFields_NoThrow()
    var
        Rec: Record "TRM Table";
    begin
        H.DoLoadFields(Rec);
        Assert.IsTrue(true, 'LoadFields must not throw');
    end;

    // ── SetLoadFields ─────────────────────────────────────────────────────────

    [Test]
    procedure SetLoadFields_NoThrow()
    var
        Rec: Record "TRM Table";
    begin
        H.DoSetLoadFields(Rec);
        Assert.IsTrue(true, 'SetLoadFields must not throw');
    end;

    // ── SetBaseLoadFields ─────────────────────────────────────────────────────

    [Test]
    procedure SetBaseLoadFields_NoThrow()
    var
        Rec: Record "TRM Table";
    begin
        H.DoSetBaseLoadFields(Rec);
        Assert.IsTrue(true, 'SetBaseLoadFields must not throw');
    end;

    // ── ReadConsistency ───────────────────────────────────────────────────────

    [Test]
    procedure ReadConsistency_ReturnsFalse()
    var
        Rec: Record "TRM Table";
    begin
        Assert.IsFalse(H.DoReadConsistency(Rec), 'ReadConsistency must return false (no SQL isolation in standalone)');
    end;

    // ── SetPermissionFilter ───────────────────────────────────────────────────

    [Test]
    procedure SetPermissionFilter_NoThrow()
    var
        Rec: Record "TRM Table";
    begin
        H.DoSetPermissionFilter(Rec);
        Assert.IsTrue(true, 'SetPermissionFilter must not throw');
    end;

    // ── Truncate ──────────────────────────────────────────────────────────────

    [Test]
    procedure Truncate_DeletesAllRows()
    var
        Rec: Record "TRM Table";
        CountAfter: Integer;
    begin
        CountAfter := H.InsertAndTruncate(Rec);
        Assert.AreEqual(0, CountAfter, 'Truncate must delete all rows');
    end;

    // ── Relation ──────────────────────────────────────────────────────────────

    [Test]
    procedure Relation_NoRelation_ReturnsZero()
    var
        Rec: Record "TRM Table";
    begin
        Assert.AreEqual(0, H.GetRelation(Rec), 'Relation on non-related field must return 0');
    end;

    // ── ReadIsolation ─────────────────────────────────────────────────────────

    [Test]
    procedure ReadIsolation_Set_NoThrow()
    var
        Rec: Record "TRM Table";
    begin
        H.SetReadIsolation(Rec);
        Assert.IsTrue(true, 'ReadIsolation assignment must not throw');
    end;

    // ── Consistent ────────────────────────────────────────────────────────────

    [Test]
    procedure Consistent_NoThrow()
    var
        Rec: Record "TRM Table";
    begin
        H.DoConsistent(Rec);
        Assert.IsTrue(true, 'Consistent must not throw');
    end;

    // ── SecurityFiltering ─────────────────────────────────────────────────────

    [Test]
    procedure SecurityFiltering_GetDefault_NoThrow()
    var
        Rec: Record "TRM Table";
        SF: SecurityFilter;
    begin
        SF := H.GetSecurityFiltering(Rec);
        Assert.IsTrue(true, 'SecurityFiltering get must not throw');
    end;

    [Test]
    procedure SecurityFiltering_Set_NoThrow()
    var
        Rec: Record "TRM Table";
    begin
        H.SetSecurityFiltering(Rec, SecurityFilter::Ignored);
        Assert.IsTrue(true, 'SecurityFiltering set must not throw');
    end;

    // ── Compilation proof ─────────────────────────────────────────────────────

    [Test]
    procedure AllMethods_Compile()
    begin
        Assert.IsTrue(true, 'All Table extra methods must compile');
    end;
}
