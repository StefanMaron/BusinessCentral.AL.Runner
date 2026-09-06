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

    local procedure FindAnyDate(var DateRec: Record Date): Boolean
    begin
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        exit(DateRec.FindSet());
    end;
}
