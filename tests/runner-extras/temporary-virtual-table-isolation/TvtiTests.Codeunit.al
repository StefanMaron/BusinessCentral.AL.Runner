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
// #3512 added the DELETE half for the Field table: the same invariant asserted over
// DeleteAll() and Delete(), because a write path can break while every read path passes.
// See the three tests' own comments; #3512 no longer reproduced when they were written, so
// they are a regression pin rather than a fix's proving test.
//
//   * NON-temporary → the populate still fires and answers truthfully. Two of the three are
//                  SENSITIVITY controls, verified to fail against a mutant that makes all
//                  three guards unconditional: Date (a range outside the default 1900-2099
//                  window, so the widening is load-bearing) and Aggregate Permission Set (its
//                  Tenant Permission Set row written AFTER the aggregate's first touch, so the
//                  per-request redrive is load-bearing). The Field one is NOT, and no attempt
//                  to make it sensitive succeeded -- a source-parsed bundle table, and Base
//                  Application Customer (18) never touched as a Record, both still pass under
//                  the mutant. See its own comment.
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

    local procedure EmptyGuid(): Guid
    var
        Empty: Guid;
    begin
        exit(Empty);
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

    // #3512 — the DELETE half of the same invariant. Codeunit 408 GlobalDimObjectNoList
    // holds a `Record "Field" temporary`, fills it, filters it by TableNo and calls
    // DeleteAll(); the runner routed that delete at the virtual Field provider and it
    // raised NavCSideRecordNotFoundException("The Field does not exist ... TableNo='83',
    // No.='52'"). Enumeration and lookup disagreed about a row of AL's OWN private store.
    //
    // Insert TWO rows under one TableNo and a third under another, so the assertions
    // discriminate between "DeleteAll deleted the filtered rows" and "DeleteAll deleted
    // everything" — a fix that simply drops the store would pass a single-row test.
    [Test]
    procedure TemporaryFieldRecordDeleteAllRemovesOnlyTheFilteredRowsAlInserted()
    var
        TempField: Record "Field" temporary;
    begin
        InsertTempFieldRow(TempField, SampleTable(), 52);
        InsertTempFieldRow(TempField, SampleTable(), 53);
        InsertTempFieldRow(TempField, OtherTable(), 52);

        TempField.Reset();
        Assert.AreEqual(3, TempField.Count(), 'temporary Record "Field": row count before DeleteAll');

        // The reported call. On the virtual Field table the walk yielded (SampleTable, 52)
        // and the delete that followed could not address it by primary key.
        TempField.SetRange(TableNo, SampleTable());
        Assert.AreEqual(2, TempField.Count(), 'temporary Record "Field": filtered row count before DeleteAll');
        TempField.DeleteAll();
        Assert.AreEqual(0, TempField.Count(), 'temporary Record "Field": filtered rows survived DeleteAll');

        // The unfiltered row must still be there: DeleteAll honours the filter, and the
        // store was never replaced wholesale.
        TempField.Reset();
        Assert.AreEqual(1, TempField.Count(), 'temporary Record "Field": row count after DeleteAll');
        Assert.IsTrue(TempField.FindFirst(), 'temporary Record "Field": the unfiltered row was deleted too');
        Assert.AreEqual(OtherTable(), TempField.TableNo, 'temporary Record "Field": TableNo of the surviving row');
        Assert.AreEqual(52, TempField."No.", 'temporary Record "Field": "No." of the surviving row');
    end;

    // The single-row Delete() sibling. DeleteAll() walks and deletes per row, so the two share
    // RecordImplementation.DeleteRecordAsync; this pins the path a test asserting only over
    // DeleteAll could leave uncovered.
    [Test]
    procedure TemporaryFieldRecordDeleteRemovesTheRowAlInserted()
    var
        TempField: Record "Field" temporary;
    begin
        InsertTempFieldRow(TempField, SampleTable(), 52);
        InsertTempFieldRow(TempField, SampleTable(), 53);

        // Get() by the full primary key, then Delete() — the row the walk yields must be
        // addressable by the key it yields it under.
        Assert.IsTrue(TempField.Get(SampleTable(), 52), 'temporary Record "Field": Get() could not address the row AL inserted');
        TempField.Delete();

        TempField.Reset();
        Assert.AreEqual(1, TempField.Count(), 'temporary Record "Field": row count after Delete');
        Assert.IsFalse(TempField.Get(SampleTable(), 52), 'temporary Record "Field": the deleted row is still addressable');
        Assert.IsTrue(TempField.Get(SampleTable(), 53), 'temporary Record "Field": Delete removed a row it was not asked to');
    end;

    // The shape codeunit 408 GlobalDimObjectNoList actually has, and the one the hand-built
    // rows above do NOT reach: the temporary buffer is filled by COPYING whole rows out of the
    // real (non-temporary) Field table — `TempField := Field; TempField.Insert()` — and only
    // then filtered and DeleteAll()'d. A copied row carries every column the virtual provider
    // built, including ones a hand-built Init() leaves at their defaults.
    [Test]
    procedure TemporaryFieldRecordFilledFromTheRealTableDeletesAll()
    var
        FieldRec: Record "Field";
        TempField: Record "Field" temporary;
        Copied: Integer;
    begin
        FieldRec.Reset();
        FieldRec.SetRange(TableNo, SampleTable());
        Assert.IsTrue(FieldRec.FindSet(), 'non-temporary Record "Field": nothing to copy from');
        repeat
            TempField := FieldRec;
            TempField.Insert();
            Copied += 1;
        until FieldRec.Next() = 0;

        TempField.Reset();
        Assert.AreEqual(Copied, TempField.Count(), 'temporary Record "Field": copied row count');
        Assert.IsTrue(Copied >= 3, 'temporary Record "Field": fewer copied rows than the 3 declared fields');

        // Codeunit 408's call, on a buffer filled exactly the way it fills one.
        TempField.SetRange(TableNo, SampleTable());
        TempField.DeleteAll();
        Assert.AreEqual(0, TempField.Count(), 'temporary Record "Field": copied rows survived DeleteAll');
        TempField.Reset();
        Assert.AreEqual(0, TempField.Count(), 'temporary Record "Field": row count after DeleteAll over the whole store');
    end;

    local procedure OtherTable(): Integer
    begin
        exit(64583);
    end;

    local procedure InsertTempFieldRow(var TempField: Record "Field"; NewTableNo: Integer; NewFieldNo: Integer)
    begin
        TempField.Init();
        TempField.TableNo := NewTableNo;
        TempField."No." := NewFieldNo;
        TempField.Insert();
    end;

    // NOT a sensitivity control, and deliberately not named as one. Unlike the Date and
    // Aggregate Permission Set controls below, this test does NOT fail when the Field
    // find-time populate is disabled.
    //
    // What IS established: NOTHING in this repository fails when it is disabled. Against a
    // mutant that makes all three guards unconditional, runner-extras produces 5 failures
    // (three Codeunit64561.Date_* plus this suite's two) and the corpus produces 0 -- none of
    // them Field-related. So the find-time Field populate is UNOBSERVED here.
    //
    // Deliberately NOT claimed: that it is inert. Whether it inserts rows at all is disputed
    // and unresolved. A probe with the counter inside InsertFieldRowsForTable and the flag
    // spanning the whole dynamic extent of EnsureFilteredFieldTablePopulated -- validated by a
    // positive control that reports 3334 find-path inserts once the temporary guard is removed
    // -- measured 0 find-path inserts in both suites, attributing every insert (37492 in
    // runner-extras, 27577 in the corpus) to the CREATION-time populate. A reviewer's probe
    // measured 39 and 10 on the find path. Both agree on the row counts per table; they
    // disagree on which populate performs them. Do not act on either number without
    // re-measuring. See #2792.
    //
    // What this test IS: a plain non-regression assertion that the Field table still answers
    // truthfully for a non-temporary record after the guard was added.
    [Test]
    procedure NonTemporaryFieldRecordStillAnswersFromRealMetadata()
    var
        FieldRec: Record "Field";
    begin
        FieldRec.Reset();
        FieldRec.SetRange(TableNo, SampleTable());
        Assert.IsTrue(FieldRec.FindSet(), 'non-temporary Record "Field": the Field table stopped answering');
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
        // Touch the aggregate BEFORE writing the tenant row. That first touch is what runs the
        // CREATION-time populate; everything after it can only come from the per-request
        // redrive. Writing the row first (as this test used to) let the creation-time populate
        // pick it up, so the assertion passed with the redrive switched off.
        AggPermSet.Reset();
        Assert.IsFalse(AggPermSet.Get(AggPermSet.Scope::Tenant, EmptyGuid(), 'TVTI-TENANT'),
            'non-temporary Record "Aggregate Permission Set": the row must not exist before it is written');

        TenantPermissionSet.Init();
        TenantPermissionSet."App ID" := EmptyGuid();
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
        // A "Period Start" range used to make the runner widen a materialised Date window, and
        // on a temporary record it widened straight into AL's own store. Since #3506 the
        // non-temporary table is served by BC's own DateDataProvider and materialises nothing,
        // so this arm now pins that a temporary Record Date still holds only what AL inserted.
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
        // January 1850 was OUTSIDE the runner's old materialised window (1900 .. 2099), so
        // answering this range used to require widening it on demand. Since #3506 BC's own
        // DateDataProvider answers it with no store at all; the year stays 1850 because a range
        // the old window happened to hold could not tell the two mechanisms apart.
        DateRec.Reset();
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", 18500101D, 18500131D);
        // Count() first, on purpose: it takes the CountAsync path, a different entry point into
        // the provider than the find below.
        Assert.AreEqual(31, DateRec.Count(), 'non-temporary Record Date: days answered for January 1850');
        Assert.IsTrue(DateRec.FindSet(), 'non-temporary Record Date: the find path stopped answering');
        Assert.AreEqual(18500101D, DateRec."Period Start", 'non-temporary Record Date: first day of January 1850');
    end;
}
