// Issue #2524 — the runner's virtual-table populates must not write into a `temporary`
// record's private store.
//
// GetDataAccessForTableCore already honours that invariant at DataAccess-creation time: its
// `if (isTemporary)` branch returns a fresh private store and skips all 18 creation-time
// populates below it. Three populates, however, re-run LATER — at find/Get time, from
// DataAccess_IsManagedFindRequest and DataAccess_AggregatePermissionSetGuardForGet — and
// those decided on the request's table id alone, which a temporary record's request carries
// just the same. One code path had the guard, the sibling path did not.
//
// Each table below gets both directions:
//   * temporary  → the store holds exactly the one row AL inserted, that row reads back the
//                  values AL wrote, and a column AL never wrote reads back its default (so a
//                  fix that fabricates values is caught, not just one that stops injecting).
//   * NON-temporary → the populate still fires and answers truthfully. Without these, a fix
//                  that simply disabled the three populates would pass every test above.
codeunit 64582 "TVTI Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "TVTI Assert";
        SampleTableNo: Integer;

    trigger OnRun()
    begin
    end;

    local procedure SampleTable(): Integer
    begin
        exit(64581);
    end;

    // ── Field (2000000041) ──────────────────────────────────────────────────────────────

    [Test]
    procedure TemporaryFieldRecordKeepsOnlyTheRowsAlInserted()
    var
        TempField: Record "Field" temporary;
    begin
        TempField.Init();
        TempField.TableNo := SampleTable();
        TempField."No." := 7;
        TempField.Insert();

        TempField.Reset();
        TempField.SetRange(TableNo, SampleTable());
        Assert.AreEqual(1, TempField.Count(), 'temporary Record "Field" row count before FindSet');
        Assert.IsTrue(TempField.FindSet(), 'temporary Record "Field": FindSet found nothing after Insert');

        // The reported symptom: "No." read back as 0 because the find-time populate had just
        // injected the real field metadata of SampleTable() and the find returned its first row.
        Assert.AreEqual(7, TempField."No.", 'temporary Record "Field": "No." read back');
        Assert.AreEqual(SampleTable(), TempField.TableNo, 'temporary Record "Field": TableNo read back');
        // Never written by AL — must still be the type default, not a fabricated metadata value.
        Assert.AreEqual('', TempField.FieldName, 'temporary Record "Field": FieldName was never written by AL');
        Assert.AreEqual(0, TempField.Next(), 'temporary Record "Field": a second row exists that AL never inserted');
        Assert.AreEqual(1, TempField.Count(), 'temporary Record "Field" row count after FindSet');
    end;

    [Test]
    procedure NonTemporaryFieldRecordStillAnswersFromRealMetadata()
    var
        FieldRec: Record "Field";
    begin
        FieldRec.Reset();
        FieldRec.SetRange(TableNo, SampleTable());
        Assert.IsTrue(FieldRec.FindSet(), 'non-temporary Record "Field": the virtual-table populate stopped firing');
        Assert.IsTrue(FieldRec.Count() >= 3, 'non-temporary Record "Field": fewer rows than the 3 declared fields of table 64581');

        Assert.IsTrue(FieldRec.Get(SampleTable(), 3), 'non-temporary Record "Field": Get(64581, 3) found nothing');
        Assert.AreEqual('Description', FieldRec.FieldName, 'non-temporary Record "Field": FieldName of field 3');
    end;

    // ── Aggregate Permission Set (2000000167) ───────────────────────────────────────────

    [Test]
    procedure TemporaryAggregatePermissionSetKeepsOnlyTheRowsAlInserted()
    var
        TempAggPermSet: Record "Aggregate Permission Set" temporary;
    begin
        TempAggPermSet.Init();
        TempAggPermSet.Scope := TempAggPermSet.Scope::System;
        TempAggPermSet."Role ID" := 'TVTI-ROLE';
        TempAggPermSet.Insert();

        TempAggPermSet.Reset();
        Assert.AreEqual(1, TempAggPermSet.Count(), 'temporary Record "Aggregate Permission Set" row count before FindSet');
        Assert.IsTrue(TempAggPermSet.FindSet(), 'temporary Record "Aggregate Permission Set": FindSet found nothing after Insert');
        Assert.AreEqual('TVTI-ROLE', TempAggPermSet."Role ID", 'temporary Record "Aggregate Permission Set": "Role ID" read back');
        // Never written by AL — the redrive used to overwrite this with a real permission set's name.
        Assert.AreEqual('', TempAggPermSet.Name, 'temporary Record "Aggregate Permission Set": Name was never written by AL');
        Assert.AreEqual(0, TempAggPermSet.Next(), 'temporary Record "Aggregate Permission Set": a second row exists that AL never inserted');
        Assert.AreEqual(1, TempAggPermSet.Count(), 'temporary Record "Aggregate Permission Set" row count after FindSet');
    end;

    [Test]
    procedure NonTemporaryAggregatePermissionSetStillRedrivesFromTenantPermissionSets()
    var
        TenantPermissionSet: Record "Tenant Permission Set";
        AggPermSet: Record "Aggregate Permission Set";
    begin
        // Aggregate Permission Set has no persistent state of its own: it is the union of the
        // Metadata and Tenant Permission Set tables, re-derived on every touch (#2473). Writing
        // a Tenant Permission Set row and then reading it back through the aggregate is what
        // proves the redrive still fires for a NON-temporary record — the half a fix that just
        // switched the populate off would break.
        TenantPermissionSet.Init();
        TenantPermissionSet."App ID" := CreateGuid();
        TenantPermissionSet."Role ID" := 'TVTI-TENANT';
        TenantPermissionSet.Name := 'TVTI tenant set';
        TenantPermissionSet.Insert();

        AggPermSet.Reset();
        AggPermSet.SetRange("Role ID", 'TVTI-TENANT');
        Assert.IsTrue(AggPermSet.FindSet(), 'non-temporary Record "Aggregate Permission Set": the redrive stopped firing');
        Assert.AreEqual('TVTI-TENANT', AggPermSet."Role ID", 'non-temporary Record "Aggregate Permission Set": "Role ID" of the redriven row');
        Assert.AreEqual('TVTI tenant set', AggPermSet.Name, 'non-temporary Record "Aggregate Permission Set": Name of the redriven row');
    end;

    // ── Date (2000000007) ───────────────────────────────────────────────────────────────

    [Test]
    procedure TemporaryDateRecordKeepsOnlyTheRowsAlInserted()
    var
        TempDate: Record Date temporary;
    begin
        TempDate.Init();
        TempDate."Period Type" := TempDate."Period Type"::Date;
        TempDate."Period Start" := 20990115D;
        TempDate."Period Name" := 'TVTI';
        TempDate.Insert();

        TempDate.Reset();
        TempDate.SetRange("Period Type", TempDate."Period Type"::Date);
        // A CLOSED "Period Start" range is what makes the runner widen its materialised Date
        // window; on a temporary record it widened straight into AL's own store.
        TempDate.SetRange("Period Start", 20990101D, 20990131D);
        Assert.AreEqual(1, TempDate.Count(), 'temporary Record Date row count before FindSet');
        Assert.IsTrue(TempDate.FindSet(), 'temporary Record Date: FindSet found nothing after Insert');
        Assert.AreEqual(20990115D, TempDate."Period Start", 'temporary Record Date: "Period Start" read back');
        Assert.AreEqual('TVTI', TempDate."Period Name", 'temporary Record Date: "Period Name" read back');
        // Never written by AL — the widening filled this in from the real calendar.
        Assert.AreEqual(0, TempDate."Period No.", 'temporary Record Date: "Period No." was never written by AL');
        Assert.AreEqual(0, TempDate.Next(), 'temporary Record Date: a second row exists that AL never inserted');
        Assert.AreEqual(1, TempDate.Count(), 'temporary Record Date row count after FindSet');
    end;

    [Test]
    procedure NonTemporaryDateRecordStillMaterialisesTheFilteredWindow()
    var
        DateRec: Record Date;
    begin
        DateRec.Reset();
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", 20990101D, 20990131D);
        Assert.IsTrue(DateRec.FindSet(), 'non-temporary Record Date: the window widening stopped firing');
        Assert.AreEqual(31, DateRec.Count(), 'non-temporary Record Date: days materialised for January 2099');
    end;
}
