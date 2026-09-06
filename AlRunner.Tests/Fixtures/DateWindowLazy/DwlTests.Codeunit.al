// Issue #2648 — the Date system virtual table (2000000007) must materialise only the periods a
// request can actually select.
//
// The runner serves Date from an in-memory store, so it has to insert rows. Before this fix it
// inserted the whole default window — 1900-01-01 to 2099-12-31, about 87,000 rows across the
// five period types — on the FIRST touch of Record Date, whatever the request asked for. A
// filter naming one week in 1850 then cost about 109,000 row inserts to return 7 rows.
//
// The row count is what these tests assert, and they assert it through the row CAP rather than
// through a clock. DateVirtualTableLazyWindowTests.cs runs this fixture with
// AL_RUNNER_DATE_WINDOW_MAX_ROWS=2000: below the ~87,000 the default window needs, above the 25
// a one-week span needs. So the first test can only pass if the window was NOT materialised,
// and the second can only pass if it still IS for a request that needs it.
//
// Nothing here asserts what the Date table SAYS — weekday numbering, ISO weeks, month ends.
// That is plain BC behaviour and lives upstream in the al-language corpus (codeunit 60983,
// "Test Date Virtual Table").

codeunit 61631 "DWL Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "DWL Assert";

    [Test]
    procedure Date_BoundedFilter_MaterialisesOnlyTheBoundedSpan()
    var
        DateRec: Record Date;
    begin
        // [GIVEN] a closed range of one week in 1850 — outside the default window entirely,
        //         so under the old eager scheme this cost the window PLUS a 50-year extension
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1850), DMY2Date(7, 1, 1850));

        // [THEN] it answers under a 2,000-row cap, which is only possible if the ~87,000-row
        //        default window was never materialised. Count() takes the count path and
        //        FindFirst() the find path, so both guards are exercised.
        Assert.AreEqual(7, DateRec.Count(), 'Expected 7 Date-type rows for 1-7 January 1850.');
        Assert.IsTrue(DateRec.FindFirst(), 'Record Date found no row for 1 January 1850.');
        Assert.AreEqual(DMY2Date(1, 1, 1850), DateRec."Period Start", 'Expected the range to start on 1 January 1850.');
        // Not merely "a row came back": 1 January 1850 was a Tuesday, so BC's Monday = 1
        // numbering makes its Period No. 2. A blank or default row fails here.
        Assert.AreEqual(2, DateRec."Period No.", '1 January 1850 was a Tuesday, so Period No. is 2.');
    end;

    [Test]
    procedure Date_KeyedGetInABoundedSpan_MaterialisesOnlyThatPeriod()
    var
        DateRec: Record Date;
    begin
        // A full-primary-key Get takes DataAccess's own primary-key route, not the find path
        // (#2870). It has to be lazy too, and it has to still answer.
        Assert.IsTrue(
            DateRec.Get(DateRec."Period Type"::Date, DMY2Date(3, 1, 1850)),
            'Record Date.Get found no row for 3 January 1850 under a 2,000-row cap.');
        Assert.AreEqual(DMY2Date(3, 1, 1850), DateRec."Period Start", 'Get returned a different period.');
    end;

    [Test]
    procedure Date_UnfilteredRead_StillDemandsTheWholeDocumentedWindow()
    var
        DateRec: Record Date;
    begin
        // THE CONTROL, and the reason this cannot be "just materialise less". A read that names
        // no closed bound on "Period Start" is answered from the WINDOW — that is the documented
        // approximation in docs/limitations.md. So an unfiltered read must still ask for the
        // whole 1900..2099 window, and under a 2,000-row cap it must refuse BY NAME rather than
        // answer from the handful of 1850 rows a sibling test may have materialised.
        //
        // If a future change made the window lazy in the sense of "never populate it at all",
        // this test goes green-to-red-to-wrong: it would answer with too few rows instead.
        asserterror FindAnyDate(DateRec);
        Assert.ExpectedError('out-of-scope: Date (virtual table 2000000007)');
        Assert.ExpectedError('past the');
    end;

    // ── Issue #3044 — IsEmpty() must not materialise the window behind a guard that has
    //    already narrowed the very same request ────────────────────────────────────────────
    //
    // Record.IsEmpty() does not take the count path: it reaches DataAccess.ExistsAsync, whose
    // guard materialises exactly what the request can select (25 rows for a closed week), and
    // ExistsAsync then calls TempTableDataProvider.Exists, where the provider-level safety net
    // materialised the whole 86,885-row default window BEHIND it. Both guards are correct and
    // neither subsumes the other; they simply stacked.
    //
    // The instrument is the row cap again, not a clock. Under AL_RUNNER_DATE_WINDOW_MAX_ROWS
    // = 2000 the stacked pair does not merely cost more, it REFUSES — measured on main before
    // this fix, this test failed with
    //
    //   a Date filter asks for periods in [1900-01-01..2099-12-31], which would add about
    //   86,885 rows for 86,910 in all, past the 2,000-row cap for the materialised window
    //   (currently 25 rows in 1 span(s), [1820-01-01..1820-01-07])
    //
    // — the two guards visible in one diagnostic. So this can only pass if the net now
    // recognises that the store already holds every row this request can select.

    [Test]
    procedure Date_IsEmptyOnABoundedSpan_DoesNotMaterialiseTheWindow()
    var
        DateRec: Record Date;
    begin
        // [GIVEN] a closed week in 1820 — outside the default 1900..2099 window entirely
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1820), DMY2Date(7, 1, 1820));

        // [THEN] IsEmpty() answers FALSE under a 2,000-row cap. Before #3044 it refused here.
        Assert.AreEqual(false, DateRec.IsEmpty(), 'Seven Date-type periods exist in 1-7 January 1820.');

        // Not merely "it did not throw": the same range still has to hold the right rows, so a
        // fix that skipped the window by materialising nothing would fail on the next two lines.
        Assert.AreEqual(7, DateRec.Count(), 'Expected 7 Date-type rows for 1-7 January 1820.');
        Assert.IsTrue(DateRec.FindFirst(), 'Record Date found no row for 1 January 1820.');
        // 1 January 1820 was a Saturday, so BC's Monday = 1 numbering makes its Period No. 6.
        Assert.AreEqual(6, DateRec."Period No.", '1 January 1820 was a Saturday, so Period No. is 6.');
    end;

    [Test]
    procedure Date_IsEmptyOnABoundedSpanThatSelectsNothing_ReturnsTrue()
    var
        DateRec: Record Date;
    begin
        // THE OTHER DIRECTION. A closed range the filter engine legitimately selects nothing
        // from: 2-7 January 1821 is Tuesday to Sunday, so it contains no Monday and therefore
        // no Week period starts in it. The fast path has to answer TRUE here — a fix that
        // returned "not empty" whenever it skipped the window would pass the test above and
        // fail this one.
        DateRec.SetRange("Period Type", DateRec."Period Type"::Week);
        DateRec.SetRange("Period Start", DMY2Date(2, 1, 1821), DMY2Date(7, 1, 1821));
        Assert.AreEqual(true, DateRec.IsEmpty(), 'No week starts between 2 and 7 January 1821.');
        Assert.AreEqual(0, DateRec.Count(), 'Count must agree with IsEmpty over the same range.');

        // And the same days DO hold Date-type periods, so the span really was materialised and
        // TRUE above came from the filter, not from an empty store.
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        Assert.AreEqual(false, DateRec.IsEmpty(), '2-7 January 1821 holds six Date-type periods.');
        Assert.AreEqual(6, DateRec.Count(), 'Expected 6 Date-type rows for 2-7 January 1821.');
    end;

    [Test]
    procedure Date_UnboundedIsEmpty_StillDemandsTheWholeDocumentedWindow()
    var
        DateRec: Record Date;
    begin
        // THE CONTROL for #3044, and the half that must NOT have changed. An IsEmpty() naming
        // no closed "Period Start" bound is still answered from the WHOLE documented window, so
        // under a 2,000-row cap it must still refuse BY NAME — even though sibling tests have
        // by now materialised several bounded spans it could have answered from.
        //
        // A fix that skipped the window unconditionally, or whenever ANY span was materialised,
        // goes green above and red here.
        asserterror IsAnyDateEmpty(DateRec);
        Assert.ExpectedError('out-of-scope: Date (virtual table 2000000007)');
        Assert.ExpectedError('past the');
    end;

    [Test]
    procedure Date_FlowFieldOverDate_StillDemandsTheWholeDocumentedWindow()
    var
        Holder: Record "DWL Date Count Holder";
    begin
        // THE SECOND CONTROL. A FlowField whose CalcFormula source is Date reaches the provider
        // WITHOUT going through DataAccess at all, so no guard has narrowed anything and the
        // provider-level net is the only thing standing between it and a wrong answer (#2988
        // measured `count(Date …)` returning 0 instead of 73,049 without it). It names no
        // closed "Period Start" bound, so it must still demand the whole window and refuse
        // under the cap.
        Holder.Code := 'X';
        Holder.Insert();
        asserterror Holder.CalcFields("Date Rows");
        Assert.ExpectedError('out-of-scope: Date (virtual table 2000000007)');
        Assert.ExpectedError('past the');
    end;

    local procedure FindAnyDate(var DateRec: Record Date): Boolean
    begin
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        exit(DateRec.FindSet());
    end;

    local procedure IsAnyDateEmpty(var DateRec: Record Date): Boolean
    begin
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        exit(DateRec.IsEmpty());
    end;
}
