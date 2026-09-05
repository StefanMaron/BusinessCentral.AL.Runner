// DateVirtualTableWindowTests — the row-count estimate that decides whether the Date virtual
// table's materialised window (2000000007, issue #2309) may be widened or has to refuse.
//
// The estimate is the only thing standing between "widen the window to cover this filter" and
// "materialise nine thousand years of dates". It has to be an OVER-estimate of the true row
// count for the span, never an under-estimate: an under-estimate lets a request through that
// then allocates more rows than the cap was meant to allow. The tests below compute the true
// count independently, day by day, and compare.

using System;
using System.Collections.Generic;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class DateVirtualTableWindowTests
{
    /// <summary>
    /// The true number of Date-table rows for the span, counted directly: one per day, one per
    /// Monday, one per month start, one per quarter start, one per 1 January. This is the
    /// definition the estimate has to bound, written out independently of it.
    /// </summary>
    private static long TrueRowCount(DateTime start, DateTime end)
    {
        long n = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            n++;                                                   // period type Date
            if (d.DayOfWeek == DayOfWeek.Monday) n++;              // Week
            if (d.Day == 1) n++;                                   // Month
            if (d.Day == 1 && d.Month % 3 == 1) n++;               // Quarter
            if (d.Day == 1 && d.Month == 1) n++;                   // Year
        }
        return n;
    }

    [Theory]
    [InlineData(2026, 1, 1, 2026, 12, 31)]     // one plain year
    [InlineData(2024, 1, 1, 2024, 12, 31)]     // a leap year
    [InlineData(1850, 1, 1, 1850, 1, 7)]       // a single week, the on-demand-widening case
    [InlineData(1999, 6, 15, 2003, 2, 28)]     // a span that starts and ends mid-period
    public void Estimate_IsAtLeastTheTrueRowCount(int y1, int m1, int d1, int y2, int m2, int d2)
    {
        var start = new DateTime(y1, m1, d1);
        var end = new DateTime(y2, m2, d2);

        var estimate = RecordPatches.EstimateDateRowCount(start, end);
        var truth = TrueRowCount(start, end);

        Assert.True(estimate >= truth,
            $"estimate {estimate} must not undercount the true {truth} rows for {start:yyyy-MM-dd}..{end:yyyy-MM-dd}");
        // And it must stay close, or the cap refuses spans it could actually have served.
        Assert.True(estimate <= truth + truth / 10 + 32,
            $"estimate {estimate} is more than 10% above the true {truth} rows for {start:yyyy-MM-dd}..{end:yyyy-MM-dd}");
    }

    [Fact]
    public void Estimate_OneCalendarYear_IsTheTrueCountRoundedUpByOneWeek()
    {
        // 2026 truly holds 434 rows: 365 days + 52 Mondays + 12 months + 4 quarters + 1 year.
        // The estimate says 435, because the week term rounds up — 365 days can hold 53 Mondays
        // and the bound has to allow for that. Rounding the other way undercounts a leap year
        // that starts on a Monday, which is the case this pins against regressing.
        Assert.Equal(434, TrueRowCount(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)));
        Assert.Equal(435, RecordPatches.EstimateDateRowCount(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)));
    }

    [Fact]
    public void Estimate_InvertedSpan_IsZero()
    {
        // Negative direction: a span whose end precedes its start describes no rows, and must
        // not produce a negative or wrapped count that would slip under the cap comparison.
        Assert.Equal(0, RecordPatches.EstimateDateRowCount(
            new DateTime(2026, 12, 31), new DateTime(2026, 1, 1)));
    }

    [Fact]
    public void DefaultWindow_FitsUnderTheDefaultRowCap()
    {
        // If this ever stops holding, the runner throws RunnerOutOfScopeException on the FIRST
        // read of Record Date in a default configuration — the window would refuse itself.
        var estimate = RecordPatches.EstimateDateRowCount(
            new DateTime(RecordPatches.DateWindowMinYearDefault, 1, 1),
            new DateTime(RecordPatches.DateWindowMaxYearDefault, 12, 31));

        Assert.True(estimate < RecordPatches.DateWindowMaxRowsDefault,
            $"the default window is about {estimate:N0} rows, at or past the "
            + $"{RecordPatches.DateWindowMaxRowsDefault:N0}-row default cap");
        // And there has to be real headroom left for on-demand widening, or the first filter
        // naming a date outside the window refuses instead of widening.
        Assert.True(estimate * 4 < RecordPatches.DateWindowMaxRowsDefault,
            $"the default window ({estimate:N0} rows) leaves too little room under the "
            + $"{RecordPatches.DateWindowMaxRowsDefault:N0}-row cap to widen on demand");
    }

    [Fact]
    public void RealBcDateSpan_IsRefusedByTheCap()
    {
        // The whole point of the cap: BC's own Date table runs from year 1 to year 9999, and
        // materialising it is what the runner must refuse rather than half-answer.
        var estimate = RecordPatches.EstimateDateRowCount(
            new DateTime(1, 1, 3), new DateTime(9999, 12, 31));

        Assert.True(estimate > RecordPatches.DateWindowMaxRowsDefault,
            $"the full BC Date span estimates at only {estimate:N0} rows, which the "
            + $"{RecordPatches.DateWindowMaxRowsDefault:N0}-row cap would let through");
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────
// The covered-interval algebra behind per-request materialisation (#2648).
//
// Before #2648 the provider recorded ONE min..max envelope, which was enough while the whole
// window was materialised up front. Per request it is not: materialise one week of 1850, then
// ask for a day in 2100, and an envelope has to fill the 250 years between — the exact ~109,000
// rows the issue is about, moved to a later call. The runner-extras Date suite does that in its
// first two tests, so the set is what makes the fix hold rather than move.
//
// DateMissingSpans decides what gets inserted and DateAddCovered decides what is remembered.
// A gap DateMissingSpans fails to report is a row that never gets materialised — an AL read
// answering "no such period" for a period that exists. So they are tested directly, against a
// brute-force day-set oracle as well as by hand.
public sealed class DateCoveredIntervalTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d);

    private static List<(DateTime Start, DateTime End)> Cover(params (int, int, int, int, int, int)[] spans)
    {
        var list = new List<(DateTime Start, DateTime End)>();
        foreach (var (y1, m1, d1, y2, m2, d2) in spans)
            RecordPatches.DateAddCovered(list, D(y1, m1, d1), D(y2, m2, d2));
        return list;
    }

    [Fact]
    public void MissingSpans_NothingCovered_IsTheWholeRequest()
    {
        var gaps = RecordPatches.DateMissingSpans(new List<(DateTime, DateTime)>(), D(1850, 1, 1), D(1850, 1, 7));
        Assert.Single(gaps);
        Assert.Equal((D(1850, 1, 1), D(1850, 1, 7)), gaps[0]);
    }

    [Fact]
    public void MissingSpans_RequestFullyInsideOneCoveredSpan_IsEmpty()
    {
        var gaps = RecordPatches.DateMissingSpans(Cover((1900, 1, 1, 2099, 12, 31)), D(1950, 1, 1), D(1950, 1, 7));
        Assert.Empty(gaps);
    }

    [Fact]
    public void MissingSpans_DistantRequest_DoesNotDragInTheGapBetween()
    {
        // THE case this type exists for. 1850 is materialised; 2100-01-01 is asked for. The gap
        // reported must be the single day, not 1850-01-08..2100-01-01.
        var gaps = RecordPatches.DateMissingSpans(Cover((1850, 1, 1, 1850, 1, 7)), D(2100, 1, 1), D(2100, 1, 1));
        Assert.Single(gaps);
        Assert.Equal((D(2100, 1, 1), D(2100, 1, 1)), gaps[0]);
    }

    [Fact]
    public void MissingSpans_RequestStraddlingTwoCoveredSpans_ReportsOnlyTheHoles()
    {
        var covered = Cover((2000, 1, 1, 2000, 1, 10), (2000, 1, 21, 2000, 1, 31));
        var gaps = RecordPatches.DateMissingSpans(covered, D(1999, 12, 28), D(2000, 2, 5));
        Assert.Equal(3, gaps.Count);
        Assert.Equal((D(1999, 12, 28), D(1999, 12, 31)), gaps[0]);
        Assert.Equal((D(2000, 1, 11), D(2000, 1, 20)), gaps[1]);
        Assert.Equal((D(2000, 2, 1), D(2000, 2, 5)), gaps[2]);
    }

    [Fact]
    public void MissingSpans_InvertedRequest_IsEmpty()
    {
        // The half-open-union shape ('..%1|%2..') produced exactly this before it was clamped.
        // It must describe no rows, never a wrapped or negative span.
        Assert.Empty(RecordPatches.DateMissingSpans(Cover((1900, 1, 1, 2099, 12, 31)), D(2099, 12, 30), D(1900, 1, 2)));
    }

    [Fact]
    public void AddCovered_TouchingSpans_Merge_AndNonTouchingDoNot()
    {
        // Adjacent by one day merges: the window materialised as two halves must read as one
        // span, or WholeWindow never becomes true and every FlowField pays for the window again.
        var merged = Cover((2000, 1, 1, 2000, 1, 10), (2000, 1, 11, 2000, 1, 20));
        Assert.Single(merged);
        Assert.Equal((D(2000, 1, 1), D(2000, 1, 20)), merged[0]);

        // One day further apart does not merge, and the list stays sorted by Start.
        var separate = Cover((2000, 1, 1, 2000, 1, 10), (2000, 1, 12, 2000, 1, 20));
        Assert.Equal(2, separate.Count);
        Assert.Equal((D(2000, 1, 1), D(2000, 1, 10)), separate[0]);
        Assert.Equal((D(2000, 1, 12), D(2000, 1, 20)), separate[1]);
    }

    [Fact]
    public void AddCovered_OutOfOrderAndOverlapping_CollapsesToOneSortedSpan()
    {
        var list = Cover((2000, 3, 1, 2000, 3, 31), (2000, 1, 1, 2000, 1, 31), (2000, 2, 1, 2000, 2, 29));
        Assert.Single(list);
        Assert.Equal((D(2000, 1, 1), D(2000, 3, 31)), list[0]);

        // A span that swallows several existing ones leaves exactly one behind.
        var swallow = Cover((2000, 1, 1, 2000, 1, 5), (2000, 2, 1, 2000, 2, 5), (2000, 3, 1, 2000, 3, 5),
                            (1999, 1, 1, 2001, 1, 1));
        Assert.Single(swallow);
        Assert.Equal((D(1999, 1, 1), D(2001, 1, 1)), swallow[0]);
    }

    [Fact]
    public void AddCovered_InvertedSpan_IsIgnored()
    {
        var list = Cover((2000, 1, 10, 2000, 1, 1));
        Assert.Empty(list);
    }

    [Fact]
    public void AddThenAskAgain_ReportsNothingMissing()
    {
        // The invariant the populate loop depends on: whatever was just materialised must not be
        // materialised a second time, or every repeat read re-walks it and re-throws duplicate
        // keys over the whole span.
        var list = Cover((1850, 1, 1, 1850, 1, 7), (2100, 1, 1, 2100, 12, 31), (1900, 1, 1, 2099, 12, 31));
        Assert.Empty(RecordPatches.DateMissingSpans(list, D(1850, 1, 1), D(1850, 1, 7)));
        Assert.Empty(RecordPatches.DateMissingSpans(list, D(1900, 1, 1), D(2100, 12, 31)));
        // ...and something genuinely outside is still reported.
        var gaps = RecordPatches.DateMissingSpans(list, D(1849, 12, 25), D(1849, 12, 31));
        Assert.Single(gaps);
        Assert.Equal((D(1849, 12, 25), D(1849, 12, 31)), gaps[0]);
    }

    [Theory]
    [InlineData(2000, 1, 1, 2000, 1, 31)]
    [InlineData(1999, 12, 20, 2000, 2, 10)]
    [InlineData(2000, 1, 11, 2000, 1, 20)]
    [InlineData(2000, 1, 6, 2000, 1, 6)]
    public void MissingSpans_AgreesWithABruteForceDaySet(int y1, int m1, int d1, int y2, int m2, int d2)
    {
        // Independent oracle: build the covered days as a set, ask which requested days are not
        // in it, and check the returned intervals describe exactly those days. This is what
        // catches an off-by-one at an interval edge, which is the failure mode that would
        // silently drop one period.
        var covered = Cover((2000, 1, 1, 2000, 1, 5), (2000, 1, 7, 2000, 1, 10), (2000, 1, 21, 2000, 1, 31));
        var coveredDays = new HashSet<DateTime>();
        foreach (var (s, e) in covered)
            for (var d = s; d <= e; d = d.AddDays(1))
                coveredDays.Add(d);

        var want = (Start: D(y1, m1, d1), End: D(y2, m2, d2));
        var expected = new List<DateTime>();
        for (var d = want.Start; d <= want.End; d = d.AddDays(1))
            if (!coveredDays.Contains(d)) expected.Add(d);

        var actual = new List<DateTime>();
        foreach (var (s, e) in RecordPatches.DateMissingSpans(covered, want.Start, want.End))
        {
            Assert.True(s <= e, $"gap [{s:yyyy-MM-dd}..{e:yyyy-MM-dd}] is inverted");
            for (var d = s; d <= e; d = d.AddDays(1)) actual.Add(d);
        }

        Assert.Equal(expected, actual);
    }
}
