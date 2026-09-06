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
    procedure Date_IsEmptyBeforeTheWindow_WidensTheWindowLikeCountDoes()
    var
        DateRec: Record Date;
    begin
        // Issue #3006. IsEmpty() is a FOURTH request path into the table, not a spelling of
        // Count(). Decompiled from Ncl.dll 28.1: NavRecord.GetALIsEmptyAsync ->
        // RecordImplementation.IsEmptyAsync -> RecordImplementation.ExistsAsync ->
        // dataAccess.ExistsAsync(new ExistsCacheRequest(...)). It never reaches CountAsync, so
        // the count guard never saw it and the window was never widened for an IsEmpty().
        //
        // Measured on main before the fix, this exact pair on consecutive lines:
        //   IsEmpty() -> TRUE      Count() -> 7
        // Same record variable, same filter, opposite answers. On a service tier the Date
        // table spans years 1 through 9999, so TRUE is a wrong answer rather than a missing
        // feature — and the quiet kind, since "this range holds no periods" is exactly what an
        // IsEmpty() returning true normally means.
        //
        // The year is 1820 rather than 1850 ON PURPOSE. The first test in this codeunit widens
        // the window down to 1850 through Count(), so an IsEmpty() over 1850 placed after it
        // would pass with no guard on the exists path at all. 1820 is named by nothing else in
        // this suite, so this test proves the same thing whatever order the suite runs in.
        //
        // [GIVEN] a closed range a century before the default window starts
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1820), DMY2Date(7, 1, 1820));

        // [WHEN] IsEmpty() asks FIRST — before anything else has widened the window for it
        // [THEN] it answers over the widened window, and the other three paths agree with it
        Assert.IsFalse(DateRec.IsEmpty(), 'Record Date.IsEmpty() reported 1-7 January 1820 as empty.');
        Assert.AreEqual(7, DateRec.Count(), 'Expected 7 Date-type rows for 1-7 January 1820.');
        Assert.IsTrue(DateRec.FindFirst(), 'Record Date found no row for 1 January 1820.');
        Assert.AreEqual(DMY2Date(1, 1, 1820), DateRec."Period Start", 'Expected the range to start on 1 January 1820.');
        // Not merely "a row came back": 1 January 1820 was a Saturday, so its "Period No." is 6
        // under BC's Monday = 1 numbering. A blank or defaulted row fails here.
        Assert.AreEqual(6, DateRec."Period No.", '1 January 1820 was a Saturday, so Period No. is 6.');
    end;

    [Test]
    procedure Date_IsEmptyInsideTheWindow_StillAnswersTrueWhenNothingMatches()
    var
        DateRec: Record Date;
    begin
        // The negative arm, and the control for the test above. A guard that widened the window
        // and then reported every range as non-empty would pass that test and fail this one.
        //
        // 3 January 1950 is a Tuesday and sits inside the default window, so it is already
        // materialised: there IS a Date period starting that day, and there is NO Week period,
        // because BC's weeks start on Monday. So the same single-day filter must answer
        // opposite things for the two period types, and both answers are about rows that exist
        // rather than about rows that were never built.
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(3, 1, 1950), DMY2Date(3, 1, 1950));
        Assert.IsFalse(DateRec.IsEmpty(), '3 January 1950 is a Date period, so IsEmpty() must be false.');
        Assert.AreEqual(1, DateRec.Count(), 'Expected exactly one Date-type row for 3 January 1950.');

        DateRec.Reset();
        DateRec.SetRange("Period Type", DateRec."Period Type"::Week);
        DateRec.SetRange("Period Start", DMY2Date(3, 1, 1950), DMY2Date(3, 1, 1950));
        Assert.IsTrue(DateRec.IsEmpty(), 'No Week period starts on Tuesday 3 January 1950, so IsEmpty() must be true.');
        Assert.AreEqual(0, DateRec.Count(), 'Expected no Week-type row starting on Tuesday 3 January 1950.');
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

    [Test]
    procedure Date_ClosedRangePastTheRowCap_ThrowsOnTheIsEmptyPathToo()
    var
        DateRec: Record Date;
    begin
        // Issue #3006, the other half. The same range through IsEmpty() instead of Count() or
        // FindFirst(). Without the exists-path guard the runner would answer FALSE from the
        // rows it happens to hold — "this eight-century range has periods in it" — which is
        // true by accident and says nothing about the range that was asked for.
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1200), DMY2Date(31, 12, 9998));

        asserterror IsEmptyRows(DateRec);
        Assert.ExpectedError('out-of-scope: Date (virtual table 2000000007)');
        Assert.ExpectedError('past the');
    end;

    [Test]
    procedure Date_RowCapRefusal_TearsThroughATryFunction_InsteadOfReadingAsFalse()
    var
        DateRec: Record Date;
        Reached: Boolean;
    begin
        // Issue #2965 — the runtime consequence of what the refusal CLAIMS, not its wording.
        //
        // All nine Date refusals used to end "see docs/scope.md".
        // ApplicationObjectBasePatches.IsPermanentOutOfScope reads the reason's FIRST token:
        //
        //     return oos != null && !oos.Reason.StartsWith("not-yet-implemented", ...);
        //
        // so under the old anchor it returned TRUE and an AL [TryFunction] trapped the refusal
        // into `false` — the silent default .claude/rules/loud-failures.md exists to prevent.
        // AL would then carry on having quietly done without the table, and the test would go
        // green. docs/scope.md is the manifest of what is permanently out of scope (SMTP, HTTP
        // egress, printing); it names no table, and real BC computes this one on demand across
        // years 1 through 9999 and never refuses a Date read at all.
        //
        // [GIVEN] a range far past the row cap — the one Date refusal AL can actually provoke
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1200), DMY2Date(31, 12, 9998));

        // [WHEN] a [TryFunction] reads it
        // [THEN] the refusal tears through rather than being reported as "it just did not work"
        asserterror Reached := TryFindFirst(DateRec);
        Assert.ExpectedError('out-of-scope: Date (virtual table 2000000007)');
        Assert.IsFalse(Reached, 'TryFindFirst must not have completed.');
    end;

    local procedure CountRows(var DateRec: Record Date): Integer
    begin
        exit(DateRec.Count());
    end;

    local procedure IsEmptyRows(var DateRec: Record Date): Boolean
    begin
        exit(DateRec.IsEmpty());
    end;

    [TryFunction]
    local procedure TryFindFirst(var DateRec: Record Date)
    begin
        DateRec.FindFirst();
    end;
}
