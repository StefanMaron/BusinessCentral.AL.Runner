// Issues #2350, #3376, #3438 and #3485 — the runner-specific half of the Integer system
// virtual table (2000000026).
//
// On the service tier the table is computed per request by IntegerDataProvider, a
// RangeBasedComputedDataProvider. Decompiled from Ncl.dll, and byte-identical in 27.0 and 28.4:
//
//     protected override IEnumerable<ReadOnlyRecordBuffer> GetValuesWithinRangeForKeyField(...)
//     {
//         if (range.GetInclusiveIntegerBounds(-1000000000, 1000000000, out var low, out var high))
//             for (int thisKey = ...; thisKey >= low && thisKey <= high; ...)
//                 yield return CreateVirtualRecordBuffer(...);
//     }
//
// Since #3485 the runner serves the table from THAT provider — BC's own, reached through BC's
// own DataAccessSource.GetVirtualDataAccess — instead of from an in-memory store it had to
// populate. What the table answers is therefore plain BC behaviour and lives upstream in the
// al-language corpus (codeunit 60368); none of it is repeated here.
//
// What is left that exists only because this is the runner is the ABSENCE of the two limits a
// materialised store forced, and this suite is those:
//
//   * no row cap. Until #3485 a request naming more rows than AL_RUNNER_INTEGER_WINDOW_MAX_ROWS
//     (500,000) was refused by name on all four request paths, because inserting them was the
//     only way to answer. Nothing is inserted now, so nothing is capped.
//   * no base window. An OPEN bound used to be answered from [-1000..100000] — 100,000 rows for
//     '>=1' against a service tier's 1,000,000,000 — and was refused outright when its closed
//     end lay outside that window, because answering from it would have reported success with
//     zero rows.
//
// Every arm below therefore asserts a LOWER BOUND past those two numbers rather than an exact
// count. The exact counts are BC's claim and are pinned upstream; what belongs here is that no
// window and no cap stand between the caller and them.

codeunit 64591 "Ivtw Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Ivtw Assert";

    [Test]
    procedure Integer_SpanFarPastTheOldRowCap_IsCountedInsteadOfRefused()
    var
        IntRec: Record Integer;
    begin
        // [GIVEN] a closed range of 900,000 rows: far inside the ±1e9 a service tier serves, and
        // past the 500,000-row cap the runner used to enforce on the find path.
        IntRec.SetRange(Number, 1, 900000);

        // [THEN] it is answered. The bound is the old cap, not the exact count — 900,000 rows
        // for [1..900000] is BC's claim, and a range's row count is pinned upstream. What is
        // asserted here is that no number the runner chose stands in front of it.
        Assert.IsTrue(CountRows(IntRec) > 500000,
            'A 900,000-row Integer range must be answered in full — the 500,000-row cap is gone since #3485.');
        Assert.IsFalse(IntRec.IsEmpty(), 'A 900,000-row Integer range is not empty.');
        Assert.IsTrue(IntRec.FindSet(), 'A 900,000-row Integer range returned no rows.');
        Assert.AreEqual(1, IntRec.Number, 'Expected the range to start at 1.');
    end;

    [Test]
    procedure Integer_SpanFarPastTheOldRowCap_IsNotRefusedOnTheIsEmptyPathEither()
    var
        IntRec: Record Integer;
    begin
        // IsEmpty() is its own request path: RecordImplementation.IsEmptyAsync builds an
        // ExistsCacheRequest and reaches DataAccess.ExistsAsync, never CountAsync (#3006 proved
        // exactly this for the Date table). It carried its own refusal, so it needs its own arm
        // proving the refusal is gone rather than merely unreachable from Count().
        IntRec.SetRange(Number, 1, 900000);

        Assert.IsFalse(IsEmptyRows(IntRec),
            'IsEmpty() over a 900,000-row Integer range must answer FALSE, not refuse.');
    end;

    [Test]
    procedure Integer_HalfOpenFilterClosedFarAboveTheOldBaseWindow_IsAnswered()
    var
        IntRec: Record Integer;
    begin
        // [GIVEN] '>=249000' — closed at one end, open at the other, with its closed end far
        // outside the old base window [-1000..100000]. This is the shape the store could not
        // follow at all: it held no row at or above 249000, so the runner refused rather than
        // report success with none.
        IntRec.SetFilter(Number, '>=249000');

        // [THEN] BC's provider substitutes its own 1e9 for the open end and the rows are there.
        Assert.IsTrue(IntRec.FindSet(), 'An open-ended Integer filter above the old window returned no rows.');
        Assert.AreEqual(249000, IntRec.Number, 'Expected the open-ended range to start at its closed end 249000.');
        Assert.IsTrue(CountRows(IntRec) > 500000,
            'An open-ended range must count past the old 500,000-row cap — its rows are computed, not stored.');
    end;

    [Test]
    procedure Integer_HalfOpenFilterClosedFarBelowTheOldBaseWindow_IsAnswered()
    var
        IntRec: Record Integer;
    begin
        // The mirror, and the one a fix written only against the upper edge would miss:
        // '..-249000' is open at the LOW end and closed below the old window's floor.
        IntRec.SetFilter(Number, '..-249000');

        Assert.IsTrue(IntRec.FindLast(), 'A low-open Integer filter below the old window returned no rows.');
        Assert.AreEqual(-249000, IntRec.Number, 'Expected the low-open range to end at its closed end -249000.');
        Assert.IsTrue(CountRows(IntRec) > 500000,
            'A low-open range must count past the old 500,000-row cap.');
    end;

    [Test]
    procedure Integer_UnboundedFilter_NoLongerStopsAtTheOldBaseWindowsEdge()
    var
        IntRec: Record Integer;
    begin
        // Until #3485 this arm asserted the opposite: that FindLast on '>=1' stopped short of
        // BC's bound, because it stopped at whatever the store had materialised — 100000 by
        // default. That was the row-count divergence docs/limitations.md recorded, and it is the
        // one this change removes. Where exactly it stops is BC's claim (the ±1e9 clamp, pinned
        // upstream); that it no longer stops at a number the runner chose is this suite's.
        IntRec.SetFilter(Number, '>=1');

        Assert.IsTrue(IntRec.FindFirst(), 'An unbounded Integer filter must be served.');
        Assert.AreEqual(1, IntRec.Number, 'Expected the unbounded range to start at 1.');

        Assert.IsTrue(IntRec.FindLast(), 'An unbounded Integer filter returned no last row.');
        Assert.IsTrue(IntRec.Number > 100000,
            'An unbounded Integer range must reach past 100000, the old base window''s upper edge.');
    end;

    [Test]
    procedure Integer_MultiRangeFilterWithAnOpenEnd_IsAnsweredOnEveryRequestPath()
    var
        IntRec: Record Integer;
    begin
        // Issue #3471 refused this shape per range, because the store could serve the first
        // range and nothing of the second — dropping a whole range silently. Which rows it
        // yields, and in what order, is BC's claim and is pinned upstream (codeunit 60368,
        // Record_Integer_MultiRangeFilter_YieldsEveryRangeNotJustTheFirst, which carried an
        // expect-oos entry until this change). What is asserted here is that neither the find
        // path nor the count path refuses it any more.
        IntRec.SetFilter(Number, '1..50|200000..');

        Assert.IsTrue(IntRec.FindSet(), 'A multi-range Integer filter with an open end returned no rows.');
        Assert.IsTrue(CountRows(IntRec) > 500000,
            'A multi-range filter with an open end must count both ranges, past the old cap.');

        IntRec.Reset();
        IntRec.SetFilter(Number, '..-200000|1..50');
        Assert.IsTrue(IntRec.FindLast(), 'A multi-range Integer filter with a low-open range returned no rows.');
        Assert.IsTrue(CountRows(IntRec) > 500000,
            'A multi-range filter with a low-open range must count both ranges, past the old cap.');
    end;

    [Test]
    procedure Integer_RangeInsideTheOldBaseWindow_StillAnswersNormally()
    var
        IntRec: Record Integer;
        Seen: Integer;
    begin
        // The control, and the reason this suite cannot pass with a provider that answers
        // everything with a huge number. A small range still yields exactly its rows, in order.
        IntRec.SetRange(Number, 10, 14);

        Assert.AreEqual(5, CountRows(IntRec), 'Expected 5 rows for Number in [10..14].');
        Assert.IsFalse(IntRec.IsEmpty(), 'Number in [10..14] is not empty.');
        Assert.IsTrue(IntRec.FindSet(), 'Record Integer found no row for Number in [10..14].');
        Assert.AreEqual(10, IntRec.Number, 'Expected the range to start at 10.');

        repeat
            Seen += 1;
        until IntRec.Next() = 0;
        Assert.AreEqual(5, Seen, 'Expected to iterate exactly 5 rows.');
    end;

    [Test]
    procedure Integer_KeyedGetFarOutsideAnyStore_StillAnswers()
    var
        IntRec: Record Integer;
    begin
        // The keyed-Get path, which reaches neither find nor count: DataAccess has its own
        // primary-key route (#2504, #2648 and #2350 each needed a separate guard there while the
        // table was materialised). 999999999 is a row no store the runner could hold would have
        // contained, so answering it is a statement that nothing is being stored.
        Assert.IsTrue(IntRec.Get(999999999), 'Number 999999999 must be readable without any row being stored.');
        Assert.AreEqual(999999999, IntRec.Number, 'Get(999999999) returned a different row.');

        Assert.IsTrue(IntRec.Get(-999999999), 'Number -999999999 must be readable too.');
        Assert.AreEqual(-999999999, IntRec.Number, 'Get(-999999999) returned a different row.');
    end;

    [Test]
    procedure Integer_TemporaryRecord_HoldsNoRowsFromTheComputedProvider()
    var
        TempIntRec: Record Integer temporary;
    begin
        // The #2524 carve-out, and the invariant most at risk from changing where the rows come
        // from: a `Record Integer temporary` is a private store holding exactly what AL put in
        // it. A handout that routed the temporary record to the computed provider too would fill
        // it with a billion rows nobody inserted.
        Assert.IsTrue(TempIntRec.IsEmpty(), 'A temporary Record Integer must hold no rows.');
        Assert.AreEqual(0, TempIntRec.Count(), 'A temporary Record Integer must count 0 rows.');
        Assert.IsFalse(TempIntRec.Get(1), 'A temporary Record Integer must not answer Get(1).');
    end;

    [Test]
    procedure Integer_ATryFunctionOverALargeRange_CompletesInsteadOfTearingThrough()
    var
        IntRec: Record Integer;
        Reached: Boolean;
    begin
        // The inverse of the arm this replaces. The row-cap refusal was anchored
        // "not-yet-implemented" precisely so it would tear through an AL [TryFunction] instead
        // of being trapped into `false` (#2965). With the cap gone there is nothing to tear
        // through, and the TryFunction completes — which is what a service tier does.
        IntRec.SetRange(Number, 1, 900000);

        Reached := TryFindSet(IntRec);

        Assert.IsTrue(Reached, 'TryFindSet over a 900,000-row Integer range must complete, not refuse.');
    end;

    local procedure CountRows(var IntRec: Record Integer): Integer
    begin
        exit(IntRec.Count());
    end;

    local procedure IsEmptyRows(var IntRec: Record Integer): Boolean
    begin
        exit(IntRec.IsEmpty());
    end;

    [TryFunction]
    local procedure TryFindSet(var IntRec: Record Integer)
    begin
        IntRec.FindSet();
    end;
}
