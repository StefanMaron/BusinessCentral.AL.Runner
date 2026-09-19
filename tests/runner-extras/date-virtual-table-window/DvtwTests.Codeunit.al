// Issues #2309, #2648, #3006, #3483 and #3506 — the runner-specific half of the Date system
// virtual table (2000000007).
//
// On the service tier the table is computed per request by DateDataProvider and spans years 1
// through 9999: about 3.6 million Date-type rows alone. The runner used to serve it from an
// in-memory store it had to fill, so it filled a WINDOW (1900-01-01 .. 2099-12-31 by default),
// widened that window at request time up to a row cap (500,000), and refused a range open at
// one end whose closed end lay outside the window. Since #3506 it hands out BC's own
// DateDataProvider instead, so no row is materialised and none of those three numbers exists.
//
// What the table itself answers — weekday numbers, ISO week numbers, month ends, "Period End"
// being a closing date, and what an open-ended "Period Start" range selects — is plain BC
// behaviour and lives upstream in the al-language corpus (codeunit 60983, "Test Date Virtual
// Table"); none of it is repeated here.
//
// So each arm below asserts a LOWER BOUND past a number the runner used to impose, never the
// exact answer: a count above the old 500,000-row cap, a first row before the window's 1900
// edge, a last row past its 2099 edge, a keyed Get eight centuries outside any store. An arm
// that named the exact count would be asserting BC's claim in the wrong repository, and would
// pass just as well against a store that happened to hold that many rows.

codeunit 64561 "Dvtw Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Dvtw Assert";

    [Test]
    procedure Date_HalfOpenRangeClosedBeforeTheOldWindow_IsAnsweredInsteadOfRefused()
    var
        DateRec: Record Date;
    begin
        // Issue #3506. `..1850-01-01` is closed at its HIGH end only, and that closed end sits
        // before the old window started. The window held no period on or before 1850, so the
        // request could not be answered from it at all: it returned zero rows until #3483, and a
        // RunnerOutOfScopeException after it. BC runs the open end back to its own first period
        // start for the period type, so there are hundreds of thousands of rows to return.
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetFilter("Period Start", '..%1', DMY2Date(1, 1, 1850));

        // Past the old 500,000-row cap AND past what the old window could hold, so neither a
        // refusal nor a windowed store can produce this number.
        Assert.IsTrue(DateRec.Count() > 500000,
            'Count() over a range closed at 1850-01-01 with its low end open must exceed the old 500,000-row cap.');
        Assert.IsFalse(DateRec.IsEmpty(), 'IsEmpty() must be false for a range reaching back to the first period start.');

        // The first row is BEFORE the old window's 1900 edge — the divergence itself, not its size.
        Assert.IsTrue(DateRec.FindFirst(), 'FindFirst() found no row in a range of hundreds of thousands.');
        Assert.IsTrue(DateRec."Period Start" < DMY2Date(1, 1, 1900),
            'The first row of an open-low range must precede the old window lower edge 1900-01-01.');
        // And it is a real period, not a defaulted row: the day it names must satisfy the filter.
        Assert.IsTrue(DateRec."Period Start" <= DMY2Date(1, 1, 1850),
            'The first row must satisfy the filter it was selected by.');
    end;

    [Test]
    procedure Date_HalfOpenRangeOpenAtTheHighEnd_ReachesPastTheOldWindowsUpperEdge()
    var
        DateRec: Record Date;
    begin
        // The mirror image, and the truncation #3506 names in its first bullet: `2026-01-16..`
        // was answered FROM the window, so a descending read stopped at 2099-12-31 instead of
        // running out to BC's own last period start. FindLast is the shape that sees it.
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetFilter("Period Start", '%1..', DMY2Date(16, 1, 2026));

        Assert.IsTrue(DateRec.Count() > 500000,
            'Count() over a range open at its high end must exceed the old 500,000-row cap.');
        Assert.IsTrue(DateRec.FindLast(), 'FindLast() found no row in an open-ended range.');
        Assert.IsTrue(DateRec."Period Start" > DMY2Date(31, 12, 2099),
            'The last row of an open-high range must lie past the old window upper edge 2099-12-31.');
    end;

    [Test]
    procedure Date_MultiRangeWithAnOpenEnd_AnswersBothRangesNotJustTheFirst()
    var
        DateRec: Record Date;
    begin
        // Issue #3483's multi-range half, now answered rather than refused. The first range sits
        // inside the old window and the second reaches past it, so a store that served the first
        // and dropped the second answered a plausible-looking 10. BC answers their union.
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetFilter("Period Start", '%1..%2|%3..',
            DMY2Date(1, 1, 2000), DMY2Date(10, 1, 2000), DMY2Date(1, 1, 2300));

        Assert.IsTrue(DateRec.Count() > 500000,
            'The union of a 10-day range and an open-ended one must exceed the old 500,000-row cap.');
        // Both ends of the union, so dropping either range fails: the first row is the first day
        // of the first range, the last row is past where the second range starts.
        Assert.IsTrue(DateRec.FindFirst(), 'FindFirst() found no row for the first range.');
        Assert.AreEqual(DMY2Date(1, 1, 2000), DateRec."Period Start", 'The union must start at the first range first day.');
        Assert.IsTrue(DateRec.FindLast(), 'FindLast() found no row for the open-ended second range.');
        Assert.IsTrue(DateRec."Period Start" > DMY2Date(1, 1, 2300),
            'The union last row must lie past 2300-01-01, i.e. inside the second range.');
    end;

    [Test]
    procedure Date_ClosedRangeFarPastTheOldRowCap_IsCountedInsteadOfRefused()
    var
        DateRec: Record Date;
    begin
        // The old row cap refused this outright — nearly nine thousand years of days is far more
        // than 500,000 rows, and materialising them was the only way the runner could answer.
        // BC counts the span arithmetically (DateDataProvider.CountPeriodsWithinRange) and
        // materialises nothing.
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1200), DMY2Date(31, 12, 9998));

        Assert.IsTrue(DateRec.Count() > 500000,
            'A range of nearly nine thousand years must count past the old 500,000-row cap rather than refusing.');
        Assert.IsFalse(DateRec.IsEmpty(), 'IsEmpty() must be false for a range of nearly nine thousand years.');
        Assert.IsTrue(DateRec.FindFirst(), 'FindFirst() found no row in a range of nearly nine thousand years.');
        Assert.AreEqual(DMY2Date(1, 1, 1200), DateRec."Period Start", 'The first row must be the range own first day.');
    end;

    [Test]
    procedure Date_ATryFunctionOverAHalfOpenRange_CompletesInsteadOfTearingThrough()
    var
        DateRec: Record Date;
        Reached: Boolean;
    begin
        // Issue #2965 pinned the opposite claim: that a Date refusal tears through an AL
        // [TryFunction] rather than being trapped into `false`. There is no refusal left to tear
        // through, and the failure mode that replaces it is the one worth pinning — a
        // [TryFunction] that returns false here would read as "the platform could not do it".
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetFilter("Period Start", '..%1', DMY2Date(1, 1, 1850));

        Reached := TryFindFirst(DateRec);

        Assert.IsTrue(Reached, 'A [TryFunction] reading a half-open Date range must succeed, not report failure.');
        Assert.IsTrue(DateRec."Period Start" <= DMY2Date(1, 1, 1850), 'The row the [TryFunction] found must satisfy the filter.');
    end;

    [Test]
    procedure Date_KeyedGetFarOutsideAnyOldWindow_StillAnswers()
    var
        DateRec: Record Date;
    begin
        // Issue #2648's arm, kept and moved further out. A keyed Get takes its own primary-key
        // route to the provider, and 1200 is eight centuries outside the old window — no
        // widening could reach it under the old cap, and BC resolves it without a store.
        Assert.IsTrue(
            DateRec.Get(DateRec."Period Type"::Date, DMY2Date(1, 1, 1200)),
            'Record Date.Get found no row for 1 January 1200.');
        Assert.AreEqual(DMY2Date(1, 1, 1200), DateRec."Period Start", 'Get returned a different period.');
    end;

    [Test]
    procedure Date_KeyedGetAtTheRepresentableBoundary_AnswersInsteadOfOverflowing()
    var
        DateRec: Record Date;
    begin
        // A CONTROL, not a RED -> GREEN: measured, it passes on origin/main too.
        //
        // #3513 reports 30 Microsoft-surface failures where a keyed Get reached
        // RecordPatches.DateMissingSpans — which added or subtracted a day from a requested
        // boundary to work out what was not materialised yet — and threw
        // ArgumentOutOfRangeException, "The added or subtracted value results in an
        // un-representable DateTime". That method is deleted with the store, so the arithmetic
        // cannot overflow any more; but this AL does not reproduce the throw on the old code
        // (checked against origin/main: PASS), so it does not close that issue and must not
        // claim to. What it does pin is the boundary itself, so a store-backed Date table
        // cannot come back without something failing here.
        //
        // BC's own DateDataProvider has no window to diff, so both boundary periods are ordinary
        // rows: 0001-01-03 is its FIRST Date period start and 9999-12-31 its last
        // (DateTimeHelper.DatePeriodStartMinimumDate / DatePeriodStartMaximumDate).
        Assert.IsTrue(
            DateRec.Get(DateRec."Period Type"::Date, DMY2Date(3, 1, 1)),
            'Record Date.Get found no row for 0001-01-03, the first Date period start.');
        Assert.AreEqual(DMY2Date(3, 1, 1), DateRec."Period Start", 'Get returned a different period at the low boundary.');

        Assert.IsTrue(
            DateRec.Get(DateRec."Period Type"::Date, DMY2Date(31, 12, 9999)),
            'Record Date.Get found no row for 9999-12-31, the last Date period start.');
        Assert.AreEqual(DMY2Date(31, 12, 9999), DateRec."Period Start", 'Get returned a different period at the high boundary.');

        // The negative arm, one day past each edge: those are NOT period starts, so Get must
        // answer false rather than throw and rather than inventing a row. 0001-01-02 precedes the
        // platform's first Date period.
        Assert.IsFalse(
            DateRec.Get(DateRec."Period Type"::Date, DMY2Date(2, 1, 1)),
            'Record Date.Get returned a row for 0001-01-02, which is before the first Date period start.');
    end;

    [Test]
    procedure Date_RangeInsideTheOldWindow_StillAnswersNormally()
    var
        DateRec: Record Date;
    begin
        // The control. Every arm above asserts a number the old store could not produce, so all
        // of them would also pass against a provider that answered nonsense in the ordinary
        // case. This one is exact, small, and inside the old window: one calendar week.
        DateRec.SetRange("Period Type", DateRec."Period Type"::Date);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1950), DMY2Date(7, 1, 1950));

        Assert.AreEqual(7, DateRec.Count(), 'Expected 7 Date-type rows for 1-7 January 1950.');
        Assert.IsFalse(DateRec.IsEmpty(), '1-7 January 1950 holds seven Date periods, so IsEmpty() must be false.');
        Assert.IsTrue(DateRec.FindFirst(), 'Record Date found no row for 1 January 1950.');
        Assert.AreEqual(DMY2Date(1, 1, 1950), DateRec."Period Start", 'Expected the range to start on 1 January 1950.');

        // The negative arm: no Week period starts on Sunday 1 January 1950, because BC's weeks
        // start on Monday. Same single-day filter, opposite answer, on rows that exist either way.
        DateRec.Reset();
        DateRec.SetRange("Period Type", DateRec."Period Type"::Week);
        DateRec.SetRange("Period Start", DMY2Date(1, 1, 1950), DMY2Date(1, 1, 1950));
        Assert.IsTrue(DateRec.IsEmpty(), 'No Week period starts on Sunday 1 January 1950, so IsEmpty() must be true.');
        Assert.AreEqual(0, DateRec.Count(), 'Expected no Week-type row starting on Sunday 1 January 1950.');
    end;

    [Test]
    procedure Date_WalkToTheHighBoundary_CompletesForEveryPeriodType()
    var
        DateRec: Record Date;
        PeriodType: Integer;
        Rows: Integer;
        Last: Date;
    begin
        // #3513. The deleted window arithmetic added or subtracted a day from a requested
        // boundary to work out what was not materialised yet, and threw
        // ArgumentOutOfRangeException when that boundary sat at the end of the representable
        // range. BC's own provider reaches the same edge by a different route -
        // ToNextPeriodStart, which does unguarded AddTicks/AddMonths/AddYears and is wrapped in
        // a catch that ends the walk - so the iteration below is what drives that edge.
        //
        // Every period type, because ToNextPeriodStart has a separate arm for each and only the
        // Date arm was covered before. What each type answers is BC's claim and is pinned
        // upstream (corpus codeunit 60983); this asserts only that the walk terminates, reaches
        // the representable edge, and yields rows - never an exact count.
        for PeriodType := 0 to 4 do begin
            Clear(DateRec);
            DateRec.Reset();
            DateRec.SetRange("Period Type", PeriodType);
            DateRec.SetFilter("Period Start", '%1..', DMY2Date(1, 1, 9999));

            Rows := 0;
            Last := 0D;
            if DateRec.FindSet() then
                repeat
                    Rows += 1;
                    Last := DateRec."Period Start";
                until (DateRec.Next() = 0) or (Rows > 1000);

            Assert.IsTrue(Rows > 0,
                StrSubstNo('Period type %1 yielded no row in the final year; the walk to the boundary found nothing.', PeriodType));
            Assert.IsTrue(Rows <= 1000,
                StrSubstNo('Period type %1 did not terminate within 1000 rows in the final year.', PeriodType));
            Assert.IsTrue(Last >= DMY2Date(1, 1, 9999),
                StrSubstNo('Period type %1 ended before the final year began.', PeriodType));
            Assert.IsTrue(Last <= DMY2Date(31, 12, 9999),
                StrSubstNo('Period type %1 produced a period start past the representable range.', PeriodType));
        end;
    end;

    [Test]
    procedure Date_KeyedGetAtEachPeriodTypesOwnBoundary_AnswersInsteadOfOverflowing()
    var
        DateRec: Record Date;
        PeriodType: Integer;
        FirstStart: Date;
        LastStart: Date;
    begin
        // #3513's own shape - a keyed Get, which is how the reported failures reached the
        // deleted code (TryGetByPrimaryKeyAsync -> DataAccess_DateWindowGuardForGet). The
        // existing boundary arm covers period type Date; each of the other four begins and ends
        // on its own date, so a Get there is a different primary key and a different walk.
        //
        // The endpoints come from the provider rather than being written in, so this stays a
        // runner-side claim: it asserts that whatever the provider reports as its first and last
        // row can then be fetched by key. The exact dates are upstream (corpus codeunit 60983).
        for PeriodType := 0 to 4 do begin
            Clear(DateRec);
            DateRec.Reset();
            DateRec.SetRange("Period Type", PeriodType);
            Assert.IsTrue(DateRec.FindFirst(),
                StrSubstNo('Period type %1 has no first row.', PeriodType));
            FirstStart := DateRec."Period Start";

            Clear(DateRec);
            DateRec.Reset();
            DateRec.SetRange("Period Type", PeriodType);
            Assert.IsTrue(DateRec.FindLast(),
                StrSubstNo('Period type %1 has no last row.', PeriodType));
            LastStart := DateRec."Period Start";

            Assert.IsTrue(FirstStart < LastStart,
                StrSubstNo('Period type %1 reported a first row at or after its last.', PeriodType));

            Clear(DateRec);
            DateRec.Reset();
            Assert.IsTrue(DateRec.Get(PeriodType, FirstStart),
                StrSubstNo('Get found no row at period type %1 first period start.', PeriodType));
            Assert.AreEqual(FirstStart, DateRec."Period Start",
                StrSubstNo('Get at period type %1 first period start returned a different period.', PeriodType));

            Clear(DateRec);
            DateRec.Reset();
            Assert.IsTrue(DateRec.Get(PeriodType, LastStart),
                StrSubstNo('Get found no row at period type %1 last period start.', PeriodType));
            Assert.AreEqual(LastStart, DateRec."Period Start",
                StrSubstNo('Get at period type %1 last period start returned a different period.', PeriodType));
        end;
    end;

    [Test]
    procedure Date_KeyedGetOnePeriodPastEachEdge_AnswersFalseInsteadOfThrowing()
    var
        DateRec: Record Date;
    begin
        // The negative direction, and the one an overflow would corrupt rather than merely
        // truncate. A Get for a date outside the table must answer false; the failure this
        // guards against is an exception escaping from the boundary arithmetic instead.
        //
        // 0001-01-01 is before the first Date period start and is not a Year period start
        // either, and 9999-12-27 is a Monday whose week would end past the representable range.
        Assert.IsFalse(DateRec.Get(DateRec."Period Type"::Date, DMY2Date(1, 1, 1)),
            'Get returned a row for 1 January of year 1, which precedes the first Date period.');

        Clear(DateRec);
        Assert.IsFalse(DateRec.Get(DateRec."Period Type"::Year, DMY2Date(1, 1, 1)),
            'Get returned a row for the year-1 Year period, which the table does not carry.');

        Clear(DateRec);
        Assert.IsFalse(DateRec.Get(DateRec."Period Type"::Week, DMY2Date(27, 12, 9999)),
            'Get returned a row for the week starting 27 December 9999, which would end past the representable range.');
    end;

    [TryFunction]
    local procedure TryFindFirst(var DateRec: Record Date)
    begin
        DateRec.FindFirst();
    end;
}
