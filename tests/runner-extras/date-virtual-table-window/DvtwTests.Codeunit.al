// Issue #2309 — the runner-specific half of the Date system virtual table (2000000007).
//
// On the service tier the table is computed per find and spans years 1 through 9999: about
// 3.6 million Date-type rows alone. The runner serves it from an in-memory store, so it has to
// materialise rows, and it cannot materialise all of them. It materialises a window (default
// 1900-01-01 to 2099-12-31) and widens that window at find time to cover any CLOSED bound an
// AL "Period Start" filter names, up to a row cap (default 500,000).
//
// The two claims below exist only because that window exists. What the rows themselves say —
// weekday numbers, ISO week numbers, month ends, "Period End" being a closing date — is plain
// BC behaviour and lives upstream in the al-language corpus (codeunit 60983, "Test Date Virtual
// Table"); none of it is repeated here.
//
// Without the find-time guard the second test below is the damaging case: the runner would
// answer a filter spanning eight centuries with the ~87,000 rows it happens to hold, and the
// caller would read a wrong "earliest matching date" from a green test.

codeunit 64561 "Dvtw Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Dvtw Assert";

    [Test]
    procedure Date_ClosedBoundBeforeTheWindow_IsMaterialisedOnDemand()
    var
        DateRec: Record Date;
    begin
        // [GIVEN] a filter naming a closed range in 1850 — before the default window starts
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1850), DMY2Date(7, 1, 1850));

        // [THEN] the window widens to cover it, and the rows are real periods, not blanks.
        // 1 January 1850 was a Tuesday, so its "Period No." is 2 under BC's Monday = 1 numbering.
        // Count() takes the count path, FindFirst() the find path, and each has to widen the
        // window on its own — Count() runs first here deliberately, so a guard wired only into
        // the find path fails this test.
        Assert.AreEqual(7, DateRec.Count(), 'Expected 7 Date-type rows for 1-7 January 1850.');
        Assert.IsTrue(DateRec.FindFirst(), 'Record Date found no row for 1 January 1850.');
        Assert.AreEqual(DMY2Date(1, 1, 1850), DateRec."Period Start", 'Expected the range to start on 1 January 1850.');
        Assert.AreEqual(2, DateRec."Period No.", '1 January 1850 was a Tuesday, so Period No. is 2.');
    end;

    [Test]
    procedure Date_KeyedGetBeforeTheWindow_MaterialisesOnDemand()
    var
        DateRec: Record Date;
    begin
        // Issue #2648. A full-primary-key Get never reaches the find path at all: DataAccess has
        // its own primary-key route straight to the provider, which is why #2504 needed a
        // separate guard there for the Aggregate Permission Set table. The Date table shares that
        // route and was left behind, so the window was never widened for a keyed read.
        //
        // Measured on main before the fix, each call in its own process so no earlier read had
        // already widened the window: this Get answered FALSE while a FindFirst over the very
        // same day answered TRUE. Same table, same period, opposite answers — and a Get returning
        // false reads as "no such period", so nothing looked wrong.
        //
        // The year is 2100 rather than 1850 ON PURPOSE, for two reasons. It sits one year past
        // the window's UPPER edge, which no other test in this suite touches, so this test proves
        // the same thing whatever order the suite runs in — the sibling test above widens the
        // window down to 1850, and a Get for 1850 placed after it would pass without the guard
        // ever running. And one year of extension is a few hundred rows rather than the ~22,000
        // a 50-year reach costs, which matters in the suite issue #2648 is about.
        //
        // [GIVEN] a period one year past the default window's upper edge
        // [WHEN]  a keyed Get names it
        // [THEN]  the window widens for the Get exactly as it does for a find
        Assert.IsTrue(
            DateRec.Get(DateRec."Period Type"::Date, DMY2Date(1, 1, 2100)),
            'Record Date.Get found no row for 1 January 2100, though a FindFirst for the same day finds one.');

        // Not merely "a row came back": the row must be the period asked for, and a real one.
        Assert.AreEqual(DMY2Date(1, 1, 2100), DateRec."Period Start", 'Get returned a different period.');
    end;

    [Test]
    procedure Date_KeyedGetInsideTheWindow_StillWorks()
    var
        DateRec: Record Date;
    begin
        // The control for the test above. A Get inside the default window never needed the guard,
        // and must keep working unchanged — a guard that widened the window on every keyed Get,
        // or that threw on one it could not parse, would fail here.
        Assert.IsTrue(
            DateRec.Get(DateRec."Period Type"::Date, DMY2Date(1, 1, 1950)),
            'Record Date.Get found no row for 1 January 1950, which is inside the default window.');
        Assert.AreEqual(DMY2Date(1, 1, 1950), DateRec."Period Start", 'Get returned a different period.');
    end;

    [Test]
    procedure Date_ClosedRangePastTheRowCap_ThrowsOutOfScope()
    var
        DateRec: Record Date;
    begin
        // [GIVEN] a filter naming a closed range of nearly nine thousand years
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1200), DMY2Date(31, 12, 9998));

        // [THEN] the runner refuses by name rather than answering from the window it happens to
        // hold. Answering would return the first row of the WINDOW as if it were the first row
        // of the RANGE — a wrong date in a passing test.
        asserterror DateRec.FindFirst();
        Assert.ExpectedError('out-of-scope: Date (virtual table 2000000007)');
        Assert.ExpectedError('past the');
    end;

    [Test]
    procedure Date_ClosedRangePastTheRowCap_ThrowsOnTheCountPathToo()
    var
        DateRec: Record Date;
    begin
        // Same range, reached through Count() instead of FindFirst(). Count() would otherwise
        // answer with the window's row count and look like a real number.
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1200), DMY2Date(31, 12, 9998));

        asserterror CountRows(DateRec);
        Assert.ExpectedError('out-of-scope: Date (virtual table 2000000007)');
    end;

    local procedure CountRows(var DateRec: Record Date): Integer
    begin
        exit(DateRec.Count());
    end;
}
