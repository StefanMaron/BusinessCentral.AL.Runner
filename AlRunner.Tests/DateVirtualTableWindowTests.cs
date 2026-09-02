// DateVirtualTableWindowTests — the row-count estimate that decides whether the Date virtual
// table's materialised window (2000000007, issue #2309) may be widened or has to refuse.
//
// The estimate is the only thing standing between "widen the window to cover this filter" and
// "materialise nine thousand years of dates". It has to be an OVER-estimate of the true row
// count for the span, never an under-estimate: an under-estimate lets a request through that
// then allocates more rows than the cap was meant to allow. The tests below compute the true
// count independently, day by day, and compare.

using System;
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
