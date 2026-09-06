// OutputIsLocaleInvariantTests — the runner's own progress and summary output must read the
// same on every machine, whatever the operator's LANG is (#2968).
//
// What was reported
// -----------------
// Two CollateralFailureReportingTests facts fail on any locale with a comma decimal separator:
//
//     Expected: ···" across 225 tests, 1 suite errors (30.9s)"
//     Actual:   ···" across 225 tests, 1 suite errors (30,9s)"
//
// on `LANG=en_DK.UTF-8`, passing under `LC_ALL=C`.
//
// The decision, and why it is the runner's side that was wrong
// -----------------------------------------------------------
// This output is machine-read, so it is INVARIANT. Three things say so and none of them is a
// preference:
//
//   * The runner already does this deliberately everywhere it writes a report a tool consumes
//     — AlCoverageReport, PhaseLog, AlDapSession all format with CultureInfo.InvariantCulture,
//     and AlDapSession's comment already names the trap by name.
//   * These lines ARE parsed: AlRunner.Tests asserts on them literally, and the summary feeds
//     --output-json, JUnit and the --watch tree.
//   * There is no localization here to preserve. The line reads "suite errors" and "AL emit" in
//     English on every machine; a comma in the middle of it is not a translation, it is the
//     operator's LANG leaking into a diagnostic.
//
// Pinning the test to `LC_ALL=C` instead would have moved the bug rather than fixed it: the
// runner would still print `30,9s` to a European developer, and the next assertion added would
// fail the same way.
//
// Deliberately NOT a global CultureInfo.DefaultThreadCurrentCulture switch at startup. The
// runner hosts BC, and AL's own Format/Evaluate/date handling is locale-sensitive BY DESIGN —
// forcing the process culture would change what the code under test does, which is a far worse
// bug than the one being fixed. So each site that writes runner output formats invariantly, and
// AL's own formatting is left alone.
//
// Test strategy
// -------------
// The culture is built by hand rather than by name. `new CultureInfo("da-DK")` silently returns
// the invariant culture when the host runs with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 — which
// is exactly how the issue reporter made the failure go away — so a test asking for it by name
// would pass vacuously on precisely the configuration that hides the bug. A cloned invariant
// culture with the separators overridden is comma-decimal everywhere, and the fixture asserts
// that before it asserts anything else.

using System.Globalization;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class OutputIsLocaleInvariantTests : IDisposable
{
    private readonly CultureInfo _saved = CultureInfo.CurrentCulture;

    /// <summary>
    /// Comma decimal separator, dot group separator, dot time separator — the shape of da-DK,
    /// nl-NL, de-DE and the reporter's own en_DK, built explicitly so it survives
    /// globalization-invariant mode.
    /// </summary>
    private static CultureInfo CommaDecimal()
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        c.NumberFormat.NumberDecimalSeparator = ",";
        c.NumberFormat.NumberGroupSeparator = ".";
        c.NumberFormat.PercentDecimalSeparator = ",";
        c.DateTimeFormat.TimeSeparator = ".";
        return c;
    }

    public OutputIsLocaleInvariantTests()
    {
        var comma = CommaDecimal();
        CultureInfo.CurrentCulture = comma;

        // The fixture must actually BE comma-decimal, or every assertion below is vacuous.
        Assert.Equal("30,9", 30.9.ToString("F1"));
        Assert.Equal("1.234", 1234.ToString("N0"));
    }

    public void Dispose() => CultureInfo.CurrentCulture = _saved;

    // ── The reported line ───────────────────────────────────────────────────────────────

    [Fact]
    public void BundleProgressLine_UsesADotDecimal_OnACommaDecimalMachine()
    {
        var line = Infrastructure.BundleProgressLine.Render(
            pass: 225, fail: 0, error: 0, tests: 225,
            suiteErrors: Array.Empty<string>(),
            elapsed: TimeSpan.FromSeconds(30.9)).Single();

        Assert.Equal("  → 225P/0F/0E across 225 tests, 0 suite errors (30.9s)", line);
    }

    // ── The siblings: the same defect at every other site that formats a number into the
    //    runner's own output. Fixing only the reported line would leave the summary a
    //    developer reads two seconds later still printing `total: 6,3s`.

    [Fact]
    public void SummaryTimings_UseADotDecimal_OnACommaDecimalMachine()
    {
        var w = new StringWriter();
        Reporter.PrintSummary(new[] { RanBucket() }, w);
        var output = w.ToString();

        Assert.Contains("AL emit:     1.5s", output, StringComparison.Ordinal);
        Assert.Contains("C# compile:  2.5s", output, StringComparison.Ordinal);
        Assert.Contains("test run:    3.5s", output, StringComparison.Ordinal);
        Assert.Contains("total:       7.5s", output, StringComparison.Ordinal);
        Assert.DoesNotContain(",5s", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureClassificationPercentages_UseADotDecimal_OnACommaDecimalMachine()
    {
        var w = new StringWriter();
        Reporter.PrintFailureClassification(new[] { BucketWithFailures(3) }, w, topN: 10);
        var output = w.ToString();

        // 3 of 3 in one classification → "100.0%", never "100,0%".
        Assert.Contains("100.0%", output, StringComparison.Ordinal);
        Assert.DoesNotContain("100,0%", output, StringComparison.Ordinal);
    }

    // Negative direction: the invariant culture must not be reached only by accident of the
    // ambient one already being invariant. Same assertions with the ambient culture restored
    // to a DOT-decimal shape must produce identical text — i.e. the output does not depend on
    // the culture in either direction.
    [Fact]
    public void OutputIsIdentical_UnderCommaAndDotCultures()
    {
        string Render()
        {
            var w = new StringWriter();
            Reporter.PrintSummary(new[] { RanBucket() }, w);
            Reporter.PrintFailureClassification(new[] { BucketWithFailures(3) }, w, topN: 10);
            return StripWallClock(w.ToString());
        }

        CultureInfo.CurrentCulture = CommaDecimal();
        var comma = Render();

        var dot = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        dot.NumberFormat.NumberDecimalSeparator = ".";
        CultureInfo.CurrentCulture = dot;
        var invariant = Render();

        Assert.Equal(invariant, comma);
        Assert.Contains("1.5s", comma, StringComparison.Ordinal);   // not vacuous
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────

    /// <summary>`wall:` is elapsed process time and differs between two renders by construction.</summary>
    private static string StripWallClock(string s) => string.Join('\n',
        s.Split('\n').Where(l => !l.TrimStart().StartsWith("wall:", StringComparison.Ordinal)));

    private static BucketResult RanBucket() => new(
        "/tmp/bundle", BucketStage.Ran, Array.Empty<string>(), null,
        new[] { new TestResult("C", "T", TestOutcome.Pass, null, null, TimeSpan.Zero) },
        TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3.5));

    private static BucketResult BucketWithFailures(int count) => new(
        "/tmp/bundle", BucketStage.Ran, Array.Empty<string>(), null,
        Enumerable.Range(0, count)
            .Select(i => new TestResult("C", "T" + i, TestOutcome.Fail,
                "Assert.AreEqual failed", null, TimeSpan.Zero))
            .ToArray(),
        TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3.5));
}
